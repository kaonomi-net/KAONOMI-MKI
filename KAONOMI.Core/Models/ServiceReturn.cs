namespace KAONOMI.Core.Models
{
    /// <summary>
    /// Return envelope for persistence operations (message, created id, success).
    /// </summary>
    public class ServiceReturn
    {
        /// <summary>User or technical message.</summary>
        public string? Message { get; set; }
        /// <summary>Identifier returned by storage (or "-1" on error).</summary>
        public string? ReturnedId { get; set; }
        /// <summary>Indicates whether the operation is considered successful.</summary>
        public bool Success { get; set; } = true;
    }
}
