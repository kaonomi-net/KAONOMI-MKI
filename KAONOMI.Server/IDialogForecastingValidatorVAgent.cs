using MARI.Framework.Core.Abstractions;
using MARI.Framework.Core.Abstractions.Attributes;
using MARI.Framework.Core.Abstractions.Attributes.MetaData;
using KAONOMI.Core;
using KAONOMI.Core.Models;
using KAONOMI.Core.Models.LLM;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace KAONOMI.Server
{
    /// <summary>
    /// Pipeline grain that validates forecasting outputs against business and consistency rules.
    /// </summary>
    [VGrainCategory("Context")]
    //[VGrainMetaData()]
    public interface IDialogForecastingValidatorVAgent : IVGrain
    {
        /// <summary>Runs forecasting validation and saves the updated dialog.</summary>
        /// <param name="d">Dialog identifier.</param>
        /// <param name="Context">MARI execution context.</param>
        /// <returns>The dialog identifier for the next pipeline step.</returns>
        Task<string> DoProcess(string d,  IExecutionContext Context);

    }
}
