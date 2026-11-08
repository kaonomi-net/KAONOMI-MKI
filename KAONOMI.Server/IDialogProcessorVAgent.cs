using MARI.Framework.Core;
using MARI.Framework.Core.Abstractions;
using MARI.Framework.Core.Abstractions.Attributes;
using KAONOMI.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
namespace KAONOMI.Server
{
    /// <summary>
    /// Pipeline grain that invokes the LLM and builds the assistant response for the dialog.
    /// </summary>
    [VGrainCategory("Collector")]
    //[VGrainMetaData()]
    public interface IDialogProcessorVAgent : IVGrain
    {
        /// <summary>Executes the main LLM call and records token usage.</summary>
        /// <param title="c">Dialog identifier.</param>
        /// <param title="Context">MARI execution context.</param>
        /// <returns>The dialog identifier for the next pipeline step.</returns>
        Task<string> DoProcess(string c, IExecutionContext Context);

    }
}
