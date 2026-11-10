using System;
using System.Collections.Generic;
using System.Linq;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// A CV (<c>/cv</c> in the chat), one document of the <c>cv</c> index: the person, a summary, the educations, the
    /// experiences, the skills and the creations. Its title is the one of every entity (<see cref="EntityBase.title"/>:
    /// "Senior developer – Dupont Jean"). A CV may be the clone of another one, rewritten for a purpose
    /// (<see cref="ClonedFrom"/>, <see cref="Adaptation"/>). The server keeps its version (one more at each save) and its
    /// date of creation. Never erased (<see cref="ISoftDeletable"/>).
    /// </summary>
    public class Cv : EntityBase, ISoftDeletable
    {
        /// <summary>Never erased: marked deleted instead (<see cref="ISoftDeletable"/>).</summary>
        public bool isDeleted { get; set; } = false;

        public string LastName { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string AddressStreet { get; set; } = string.Empty;
        public string AddressNumber { get; set; } = string.Empty;
        public string AddressCity { get; set; } = string.Empty;
        public string AddressZip { get; set; } = string.Empty;
        public string AddressCountry { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string LinkedInProfile { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;

        /// <summary>The photo of the person: the path of its file on the machine of the chat (a copy kept in Pictures/kaonomi/cv); empty: none.</summary>
        public string Photo { get; set; } = string.Empty;

        public List<Education> Educations { get; set; } = new List<Education>();
        public List<Experience> Experiences { get; set; } = new List<Experience>();
        public List<string> Skills { get; set; } = new List<string>();
        public List<Creation> Creations { get; set; } = new List<Creation>();

        /// <summary>The id of the CV this one was cloned from (empty: not a clone).</summary>
        public string ClonedFrom { get; set; } = string.Empty;

        /// <summary>What the clone was rewritten for: the adaptation asked when it was cloned.</summary>
        public string Adaptation { get; set; } = string.Empty;

        /// <summary>When the CV was created (<see cref="EntityBase.date"/> is the date of its last version).</summary>
        public DateTime? Created { get; set; }

        /// <summary>The version of the CV: 1 at its creation, one more at each save.</summary>
        public int Version { get; set; }

        /// <summary>
        /// The CV made ready to be saved: its texts trimmed, its lists never null, its empty lines left out, its title
        /// the name of the person when none is given ("CV" when there is no name either).
        /// </summary>
        public void Normalize()
        {
            LastName = (LastName ?? string.Empty).Trim();
            FirstName = (FirstName ?? string.Empty).Trim();
            Email = (Email ?? string.Empty).Trim();
            AddressStreet = (AddressStreet ?? string.Empty).Trim();
            AddressNumber = (AddressNumber ?? string.Empty).Trim();
            AddressCity = (AddressCity ?? string.Empty).Trim();
            AddressZip = (AddressZip ?? string.Empty).Trim();
            AddressCountry = (AddressCountry ?? string.Empty).Trim();
            Phone = (Phone ?? string.Empty).Trim();
            LinkedInProfile = (LinkedInProfile ?? string.Empty).Trim();
            Summary = (Summary ?? string.Empty).Trim();
            Photo = (Photo ?? string.Empty).Trim();
            ClonedFrom = (ClonedFrom ?? string.Empty).Trim();
            Adaptation = (Adaptation ?? string.Empty).Trim();
            Skills = (Skills ?? new List<string>()).Select(s => (s ?? string.Empty).Trim()).Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Educations = (Educations ?? new List<Education>()).Where(e => e is not null).ToList();
            Experiences = (Experiences ?? new List<Experience>()).Where(e => e is not null).ToList();
            Creations = (Creations ?? new List<Creation>()).Where(c => c is not null).ToList();
            foreach (var experience in Experiences)
            {
                experience.Technologies = (experience.Technologies ?? new List<string>()).Select(t => (t ?? string.Empty).Trim()).Where(t => t.Length > 0).ToList();
            }
            title = (title ?? string.Empty).Trim();
            if (title.Length == 0)
            {
                var name = (LastName + " " + FirstName).Trim();
                title = name.Length > 0 ? name : "CV";
            }
        }
    }

    public class Experience
    {
        public string Company { get; set; } = string.Empty;
        public string Position { get; set; } = string.Empty;
        public DateTime? StartDate { get; set; }
        /// <summary>Null: the position held today.</summary>
        public DateTime? EndDate { get; set; }
        public string Description { get; set; } = string.Empty;
        public List<string> Technologies { get; set; } = new List<string>();
    }

    public class Creation
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    public class Education
    {
        public string Institution { get; set; } = string.Empty;
        public string Degree { get; set; } = string.Empty;
        public string FieldOfStudy { get; set; } = string.Empty;
        public DateTime? StartDate { get; set; }
        /// <summary>Null: still going on.</summary>
        public DateTime? EndDate { get; set; }
        public string Description { get; set; } = string.Empty;
    }
}
