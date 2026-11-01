using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models
{
    public class Ontology : EntityBase, ISoftDeletable, ITagged
    {
        private static readonly HashSet<string> ValidConfidentialityLevels = new(StringComparer.OrdinalIgnoreCase)
        {
            "PUBLIC(PU)", "RESTRICTED(NR)", "SECRET(NS)", "COSMIC TOP SECRET(CTS)"
        };

        private string _confidentiality = "UNCLASSIFIED";

        public string id { get; set; } = string.Empty;
        public string dateCreated { get; set; } = string.Empty;
        public string? name { get; set; }
        public string? EntityType { get; set; }
        public string? description { get; set; }
        public string? nodeparentId { get; set; }
        public List<string> nodevirtualeftchildrenId { get; set; }
        public List<string>? sentencesExemples { get; set; }
        public int? AngryLevel { get; set; }
        public int? SadLevel { get; set; }
        public int? HappyLevel { get; set; }
        public int? FearLevel { get; set; }
        public int? SurpriseLevel { get; set; }
        public int? DisgustLevel { get; set; }
        public bool isOntologyClass { get; set; } = false;
        public string? Symbol { get; set; }
        public List<string> synonyms { get; set; }
        public List<string> Tags { get; set; }
        public List<string> relatedLinks { get; set; }
        public List<string> relatedEntities { get; set; }
        public bool isDeleted { get; set; }

        /// <summary>Optional date from which the entity holds (exists, is valid); null: no start.</summary>
        public DateTime? startDate { get; set; }

        /// <summary>Optional date until which the entity holds; null: no end.</summary>
        public DateTime? endDate { get; set; }

        public string Confidentiality
        {
            get => _confidentiality;
            set
            {
                if (!string.IsNullOrWhiteSpace(value) && ValidConfidentialityLevels.Contains(value.Trim()))
                {
                    // Levels are always stored in upper case.
                    _confidentiality = value.Trim().ToUpperInvariant();
                }
                else
                {
                    _confidentiality = "UNCLASSIFIED";
                }
            }
        }

        public int confidentialityLevel
        {
            get
            {
                return _confidentiality switch
                {
                    "PUBLIC(PU)" => 1,
                    "RESTRICTED(NR)" => 2,
                    "SECRET(NS)" => 3,
                    "COSMIC TOP SECRET(CTS)" => 4,
                    _ => 0, // UNCLASSIFIED or any other invalid value
                };
            }
            set
            {
                _confidentiality = value switch
                {
                    1 => "PUBLIC(PU)",
                    2 => "RESTRICTED(NR)",
                    3 => "SECRET(NS)",
                    4 => "COSMIC TOP SECRET(CTS)",
                    _ => "UNCLASSIFIED", // Default for any other value
                };
            }
        }

        public Ontology()
        {
            synonyms = new List<string>();
            sentencesExemples = new List<string>();
            Tags = new List<string>();
            relatedLinks = new List<string>();
            id = string.Empty;
            dateCreated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            isDeleted = false;
            Confidentiality = "PUBLIC(PU)"; // Utilise le setter pour valider la valeur par défaut
        }
    }
}