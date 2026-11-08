using MARI.Framework.Core.Abstractions;
using MARI.Framework.Core.Abstractions.Attributes;

namespace KAONOMI.Metacognition
{
    /// <summary>Virtual grain contract for the Metacognition actions stage.</summary>
    [VGrainCategory("Actions")]
    public interface iMetaCognitionActionsVAgent : IVGrain
    {
        /// <summary>Applies follow-up actions and returns the sequence input.</summary>
        /// <param title="input">Current sequence input.</param>
        /// <param title="context">MARI execution context.</param>
        /// <returns>The input string after actions complete.</returns>
        Task<string> DoProcess(string input, IExecutionContext context);
    }
}
