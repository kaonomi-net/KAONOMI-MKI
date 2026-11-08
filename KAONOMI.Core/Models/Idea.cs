using System.Collections.Generic;
namespace KAONOMI.Core.Models;

public class Idea : EntityBase, ISoftDeletable, ITagged
{
    /// <summary>Never erased: marked deleted instead (<see cref="ISoftDeletable"/>).</summary>
    public bool isDeleted { get; set; } = false;

    public string ideaTitle { get; set; }
    public string ideaDescription { get; set; } 
    public string category { get; set; }
}