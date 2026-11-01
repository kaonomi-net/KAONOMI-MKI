using MARI.Framework.Core.Abstractions;
using MARI.Framework.Core.Abstractions.Attributes;

namespace KAONOMI.Metacognition
{
    /// <summary>Virtual grain contract for the Metacognition collection stage.</summary>
    [VGrainCategory("Collection")]
    public interface iMetaCognitionCollecVAgent : IVGrain
    {
        /// <summary>Collects context inputs and passes the sequence string to the next step.</summary>
        /// <param name="input">Current sequence input.</param>
        /// <param name="context">MARI execution context.</param>
        /// <returns>The input string after collection completes.</returns>
        Task<string> DoProcess(string input, IExecutionContext context);
    }
}
