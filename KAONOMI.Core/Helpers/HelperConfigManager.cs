

namespace KAONOMI.Core.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;

    /// <summary>
    /// Loads configuration objects from a JSON file.
    /// </summary>
    public static class HelperConfigManager
    {
        /// <summary>Reads the JSON file and deserializes to <typeparamref name="T"/>; creates an empty instance if JSON is null.</summary>
        /// <param name="fullName">Full path of the file.</param>
        public static T? GetFromFile<T>(string fullName) where T : new()
        {
            try
            {
                string json = File.ReadAllText(fullName);

                return JsonSerializer.Deserialize<T>(json) ?? new T();
            }
            catch (Exception)
            {
                throw;
            }
        }
    }
}
