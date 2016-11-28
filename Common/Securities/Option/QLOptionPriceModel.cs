﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QLNet;
using QuantConnect.Util;

namespace QuantConnect.Securities.Option
{
    using Logging;
    using PricingEngineFunc = Func<GeneralizedBlackScholesProcess, IPricingEngine>;
    using PricingEngineFuncEx = Func<Symbol, GeneralizedBlackScholesProcess, IPricingEngine>;

    /// <summary>
    /// Provides QuantLib(QL) implementation of <see cref="IOptionPriceModel"/> to support major option pricing models, available in QL. 
    /// </summary>
    class QLOptionPriceModel : IOptionPriceModel
    {
        private readonly IQLUnderlyingVolatilityEstimator _underlyingVolEstimator;
        private readonly IQLRiskFreeRateEstimator _riskFreeRateEstimator;
        private readonly IQLDividendYieldEstimator _dividendYieldEstimator;
        private readonly PricingEngineFuncEx _pricingEngineFunc;

        /// <summary>
        /// Method constructs QuantLib option price model with necessary estimators of underlying volatility, risk free rate, and underlying dividend yield
        /// </summary>
        /// <param name="pricingEngineFunc">Function modeled stochastic process, and returns new pricing engine to run calculations for that option</param>
        /// <param name="underlyingVolEstimator">The underlying volatility estimator</param>
        /// <param name="riskFreeRateEstimator">The risk free rate estimator</param>
        /// <param name="dividendYieldEstimator">The underlying dividend yield estimator</param>
        public QLOptionPriceModel(PricingEngineFunc pricingEngineFunc, IQLUnderlyingVolatilityEstimator underlyingVolEstimator, IQLRiskFreeRateEstimator riskFreeRateEstimator, IQLDividendYieldEstimator dividendYieldEstimator)
        {
            _pricingEngineFunc = (option, process) => pricingEngineFunc(process);
            _underlyingVolEstimator = underlyingVolEstimator ?? new DefaultQLUnderlyingVolatilityEstimator();
            _riskFreeRateEstimator = riskFreeRateEstimator ?? new DefaultQLRiskFreeRateEstimator();
            _dividendYieldEstimator = dividendYieldEstimator ?? new DefaultQLDividendYieldEstimator();
        }
        /// <summary>
        /// Method constructs QuantLib option price model with necessary estimators of underlying volatility, risk free rate, and underlying dividend yield
        /// </summary>
        /// <param name="pricingEngineFunc">Function takes option and modeled stochastic process, and returns new pricing engine to run calculations for that option</param>
        /// <param name="underlyingVolEstimator">The underlying volatility estimator</param>
        /// <param name="riskFreeRateEstimator">The risk free rate estimator</param>
        /// <param name="dividendYieldEstimator">The underlying dividend yield estimator</param>
        public QLOptionPriceModel(PricingEngineFuncEx pricingEngineFunc, IQLUnderlyingVolatilityEstimator underlyingVolEstimator, IQLRiskFreeRateEstimator riskFreeRateEstimator, IQLDividendYieldEstimator dividendYieldEstimator)
        {
            _pricingEngineFunc = pricingEngineFunc;
            _underlyingVolEstimator = underlyingVolEstimator ?? new DefaultQLUnderlyingVolatilityEstimator();
            _riskFreeRateEstimator = riskFreeRateEstimator ?? new DefaultQLRiskFreeRateEstimator();
            _dividendYieldEstimator = dividendYieldEstimator ?? new DefaultQLDividendYieldEstimator();
        }

        /// <summary>
        /// Evaluates the specified option contract to compute a theoretical price, IV and greeks
        /// </summary>
        /// <param name="security">The option security object</param>
        /// <param name="slice">The current data slice. This can be used to access other information
        /// available to the algorithm</param>
        /// <param name="contract">The option contract to evaluate</param>
        /// <returns>An instance of <see cref="OptionPriceModelResult"/> containing the theoretical
        /// price of the specified option contract</returns>
        public OptionPriceModelResult Evaluate(Security security, Slice slice, OptionContract contract)
        {
            // setting up option pricing parameters
            var calendar = new UnitedStates();
            var dayCounter = new Actual365Fixed();
            var optionSecurity = (Option)security;

            var settlementDate = contract.Time.Date.AddDays(Option.DefaultSettlementDays);
            var maturityDate = contract.Expiry.Date.AddDays(Option.DefaultSettlementDays);
            var underlyingQuoteValue = new SimpleQuote((double)optionSecurity.Underlying.Price);

            var dividendYieldValue = new SimpleQuote(_dividendYieldEstimator.Estimate(security, slice, contract));
            var dividendYield = new Handle<YieldTermStructure>( new FlatForward(settlementDate, dividendYieldValue, dayCounter));

            var riskFreeRateValue = new SimpleQuote(_riskFreeRateEstimator.Estimate(security, slice, contract));
            var riskFreeRate = new Handle<YieldTermStructure>(new FlatForward(settlementDate, riskFreeRateValue, dayCounter));

            var underlyingVolValue = new SimpleQuote(_underlyingVolEstimator.Estimate(security, slice, contract));
            var underlyingVol = new Handle<BlackVolTermStructure>( new BlackConstantVol(settlementDate, calendar, new Handle<Quote>(underlyingVolValue), dayCounter));
            
            // preparing stochastic process and payoff functions
            var stochasticProcess = new BlackScholesMertonProcess(new Handle<Quote>(underlyingQuoteValue), dividendYield, riskFreeRate, underlyingVol);
            var payoff = new PlainVanillaPayoff(contract.Right == OptionRight.Call? QLNet.Option.Type.Call : QLNet.Option.Type.Put, (double)contract.Strike);

            // creating option QL object
            var option = contract.Symbol.ID.OptionStyle == OptionStyle.American? 
                        new VanillaOption(payoff, new AmericanExercise(settlementDate, maturityDate)):
                        new VanillaOption(payoff, new EuropeanExercise(maturityDate));

            Settings.setEvaluationDate(settlementDate);

            // preparing pricing engine QL object
            option.setPricingEngine(_pricingEngineFunc(contract.Symbol, stochasticProcess));

            // running calculations
            var npv = option.NPV();

            // function extracts QL greeks catching exception if greek is not generated by the pricing engine and reevaluates option to get numerical estimate of the seisitivity
            Func<Func<double>, Func<double>, decimal> tryGetGreekOrReevaluate = (greek, reevalFunc) =>
            {
                try
                {
                    return (decimal)greek();
                }
                catch (Exception err)
                {
                    return optionSecurity.EnableGreekApproximation ? (decimal)reevalFunc() : 0.0m;
                }
            };

            // function extracts QL greeks catching exception if greek is not generated by the pricing engine
            Func<Func<double>, decimal> tryGetGreek = greek => tryGetGreekOrReevaluate(greek, () => 0.0);

            // function extracts QL IV catching exception if IV is not generated by the pricing engine
            Func<decimal> tryGetImpliedVol = () =>
            {
                try
                {
                    return (decimal)option.impliedVolatility((double)optionSecurity.Price, stochasticProcess);
                }
                catch (Exception err)
                {
                    Log.Debug("tryGetImpliedVol() error: " + err.Message);
                    return 0m;
                }
            };

            Func<Tuple<decimal, decimal>> evalDeltaGamma = () =>
            {
                try
                {
                    return Tuple.Create((decimal)option.delta(), (decimal)option.gamma());
                }
                catch (Exception)
                {
                    if (optionSecurity.EnableGreekApproximation)
                    {
                        var step = 0.01;
                        var initial = underlyingQuoteValue.value();
                        underlyingQuoteValue.setValue(initial - step);
                        var npvMinus = option.NPV();
                        underlyingQuoteValue.setValue(initial + step);
                        var npvPlus = option.NPV();
                        underlyingQuoteValue.setValue(initial);

                        return Tuple.Create((decimal)((npvPlus - npvMinus) / (2 * step)),
                                            (decimal)((npvPlus - 2 * npv + npvMinus) / (step * step)));
                    }
                    else
                        return Tuple.Create(0.0m, 0.0m);
                }
            };

            Func<double> reevalVega = () =>
            {
                var step = 0.001;
                var initial = underlyingVolValue.value();
                underlyingVolValue.setValue(initial + step);
                var npvPlus = option.NPV();
                underlyingVolValue.setValue(initial);

                return (npvPlus - npv ) / step;
            };

            Func<double> reevalTheta = () =>
            {
                var step = 1.0 / 365.0;

                /*Settings.setEvaluationDate(settlementDate.AddDays(-1));
                var npvMinus = option.NPV();
                Settings.setEvaluationDate(settlementDate);

                return (npv - npvMinus) / step;*/

                // This code is a workaround for the bug in QLNet
                var newSettlementDate = settlementDate.AddDays(-1);

                var newDividendYield = new Handle<YieldTermStructure>(new FlatForward(newSettlementDate, dividendYieldValue, dayCounter));

                var newRiskFreeRate = new Handle<YieldTermStructure>(new FlatForward(newSettlementDate, riskFreeRateValue, dayCounter));

                var newUnderlyingVol = new Handle<BlackVolTermStructure>(new BlackConstantVol(newSettlementDate, calendar, new Handle<Quote>(underlyingVolValue), dayCounter));

                // preparing stochastic process and payoff functions
                var newStochasticProcess = new BlackScholesMertonProcess(new Handle<Quote>(underlyingQuoteValue), newDividendYield, newRiskFreeRate, newUnderlyingVol);
                var newPayoff = new PlainVanillaPayoff(contract.Right == OptionRight.Call ? QLNet.Option.Type.Call : QLNet.Option.Type.Put, (double)contract.Strike);

                // creating option QL object
                var newOption = contract.Symbol.ID.OptionStyle == OptionStyle.American ?
                            new VanillaOption(newPayoff, new AmericanExercise(newSettlementDate, maturityDate)) :
                            new VanillaOption(newPayoff, new EuropeanExercise(maturityDate));

                Settings.setEvaluationDate(newSettlementDate);

                // preparing pricing engine QL object
                newOption.setPricingEngine(_pricingEngineFunc(contract.Symbol, newStochasticProcess));

                // running calculations
                var npvMinus = newOption.NPV();

                return (npv - npvMinus) / step;
            };

            Func<double> reevalRho = () =>
            {
                var step = 0.001;
                var initial = riskFreeRateValue.value();
                riskFreeRateValue.setValue(initial + step);
                var npvPlus = option.NPV();
                riskFreeRateValue.setValue(initial);

                return (npvPlus - npv) / step;
            };

            // producing output with lazy calculations of IV and greeks

            return new OptionPriceModelResult((decimal)npv,
                        tryGetImpliedVol,
                        () => new Greeks(evalDeltaGamma,
                                        () => tryGetGreekOrReevaluate(() => option.vega(), reevalVega),
                                        () => tryGetGreekOrReevaluate(() => option.theta(), reevalTheta),
                                        () => tryGetGreekOrReevaluate(() => option.rho(), reevalRho),
                                        () => tryGetGreek(() => option.elasticity())));
        }

    }
}