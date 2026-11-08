using MARI.Framework.Core.Abstractions;
using MARI.Framework.Core.Abstractions.Attributes;

namespace KAONOMI.Reflex
{
    /// <summary>Virtual grain contract for the Reflex analysis stage.</summary>
    [VGrainCategory("Analysis")]
    public interface iReflexAnalysisVAgent : IVGrain
    {
        /// <summary>Analyzes collected context and passes the sequence string to the next step.</summary>
        /// <param title="input">Current sequence input.</param>
        /// <param title="context">MARI execution context.</param>
        /// <returns>The input string after analysis completes.</returns>
        Task<string> DoProcess(string input, IExecutionContext context);
    }
}
