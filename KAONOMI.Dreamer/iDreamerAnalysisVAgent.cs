using MARI.Framework.Core.Abstractions;
using MARI.Framework.Core.Abstractions.Attributes;

namespace KAONOMI.Dreamer
{
    /// <summary>Virtual grain contract for the Dreamer analysis stage.</summary>
    [VGrainCategory("Analysis")]
    public interface iDreamerAnalysisVAgent : IVGrain
    {
        /// <summary>Analyzes collected context and passes the sequence string to the next step.</summary>
        /// <param name="input">Current sequence input.</param>
        /// <param name="context">MARI execution context.</param>
        /// <returns>The input string after analysis completes.</returns>
        Task<string> DoProcess(string input, IExecutionContext context);
    }
}
