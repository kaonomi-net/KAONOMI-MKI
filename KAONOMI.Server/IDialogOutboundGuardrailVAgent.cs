using MARI.Framework.Core.Abstractions;
using MARI.Framework.Core.Abstractions.Attributes;
using MARI.Framework.Core.Abstractions.Attributes.MetaData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KAONOMI.Core.Models;
using KAONOMI.Core.Models.LLM;

namespace KAONOMI.Server
{
    /// <summary>
    /// Pipeline grain that filters and moderates assistant output before it is returned to the client.
    /// </summary>
    [VGrainCategory("Feedback")]
    public interface IDialogOutboundGuardrailVAgent : IVGrain
    {
        /// <summary>Runs outbound guardrail checks and saves the dialog.</summary>
        /// <param title="c">Dialog identifier.</param>
        /// <param title="Context">MARI execution context.</param>
        /// <returns>The dialog identifier for the next pipeline step.</returns>
        Task<string> DoProcess(string c, IExecutionContext Context);
    }
}
