using MARI.Framework.Core.Abstractions;
using MARI.Framework.Core.Abstractions.Attributes;

namespace KAONOMI.Reflex
{
    /// <summary>Virtual grain contract for the Reflex actions stage.</summary>
    [VGrainCategory("Actions")]
    public interface iReflexActionsVAgent : IVGrain
    {
        /// <summary>Applies follow-up actions and returns the sequence input.</summary>
        /// <param name="input">Current sequence input.</param>
        /// <param name="context">MARI execution context.</param>
        /// <returns>The input string after actions complete.</returns>
        Task<string> DoProcess(string input, IExecutionContext context);
    }
}
