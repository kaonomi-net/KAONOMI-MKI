using System.Collections.Generic;
namespace KAONOMI.Core.Models;

public class Document : EntityBase, ISoftDeletable, ITagged
{
    /// <summary>A new document is named "noname" until it is given a title.</summary>
    public Document()
    {
        title = "noname";
    }

    public string? author { get; set; } 
    public string category { get; set; } // "math", "text", "physic", "webpage"
    public string? description { get; set; } 
    public List<Content> contents  { get; set; } 
    public string? securityClassification { get; set; }
    public bool isDeleted { get; set; } = false;
    
}

public class Content
{
    public string content { get; set; }
    public int version { get; set; }
    public DateTime date { get; set; }
}
    