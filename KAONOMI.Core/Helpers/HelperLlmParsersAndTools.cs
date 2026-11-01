using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KAONOMI.Core.Models;
using KAONOMI.Core.Models.LLM;

namespace KAONOMI.Core.Helpers
{
    /// <summary>
    /// Extracts system and user prompts from generic dialog request payloads.
    /// </summary>
    public static class HelperLlmParsersAndTools
    {
        /// <summary>Returns the content of the last user message in the request, or empty if none.</summary>
        public static string GetUserPromptFromRequest(DialogGenericRequest request)
        {
            string r = "";

            try
            {
                // The current question is the last user message; earlier ones are conversation history.
                for (int i = request.messages.Count - 1; i >= 0; i--)
                {
                    Message msg = request.messages[i];
                    if (msg.role == "user") return msg.content;
                }
            }
            catch (Exception e)
            {
                r = "";
            }

            return r;
        }

        /// <summary>Returns the content of the first system message in the request, or empty if none.</summary>
        public static string GetSystemPromptFromRequest(DialogGenericRequest request)
        {
            string r = "";

            try
            {
                foreach (Message msg in request.messages)
                {
                    if (msg.role == "system") return msg.content;

                }
            }
            catch (Exception e)
            {
                r = "";
            }

            return r;
        }
    }
}
