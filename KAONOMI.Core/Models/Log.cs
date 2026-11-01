using System;
namespace KAONOMI.Core.Models
{
    /// <summary>
    /// Application log entry with controlled severity level (INFO, ERROR, etc.).
    /// </summary>
    public class Log : EntityBase
    {
        //TODO
        private string _Type = "";
        /// <summary>Creates a log with a generated identifier.</summary>
        public Log() : base()
        {
            Init();
        }
        /// <summary>Creates a log with the given identifier.</summary>
        public Log(string? id) : base(id)
        {
            Init();
        }

        /// <summary>
        /// Log type: normalized to uppercase; allowed values INFO, ERROR, WARNING, ALERT, OK, DONE, PERF (otherwise INFO).
        /// </summary>
        public string Type
        {
            get { return _Type; }
            set
            {
                value = value.ToUpper().Trim();
                if (!(value == "INFO" || value == "ERROR" || value == "WARNING" || value == "ALERT" || value == "OK" || value == "DONE" || value == "PERF"))
                {
                    _Type = "INFO";
                }
                else
                {
                    _Type = value;
                }
            }
        }

        private void Init()
        {

        }
    }
}
