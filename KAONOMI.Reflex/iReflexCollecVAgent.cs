using MARI.Framework.Core.Abstractions;
using MARI.Framework.Core.Abstractions.Attributes;

namespace KAONOMI.Reflex
{
    /// <summary>Virtual grain contract for the Reflex collection stage.</summary>
    [VGrainCategory("Collection")]
    public interface iReflexCollecVAgent : IVGrain
    {
        /// <summary>Collects context inputs and passes the sequence string to the next step.</summary>
        /// <param title="input">Current sequence input.</param>
        /// <param title="context">MARI execution context.</param>
        /// <returns>The input string after collection completes.</returns>
        Task<string> DoProcess(string input, IExecutionContext context);
    }
}
