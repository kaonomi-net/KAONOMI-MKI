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
    /// Pipeline grain that formats and enriches the LLM response after generation.
    /// </summary>
    [VGrainCategory("Context")]
    //[VGrainMetaData()]
    public interface IDialogPostProcessingVAgent : IVGrain
    {
        /// <summary>Runs post-processing on the dialog and saves changes.</summary>
        /// <param title="d">Dialog identifier.</param>
        /// <param title="Context">MARI execution context.</param>
        /// <returns>The dialog identifier for the next pipeline step.</returns>
        Task<string> DoProcess(string d,  IExecutionContext Context);

    }
}
