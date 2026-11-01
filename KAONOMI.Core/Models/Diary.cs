using System.Collections.Generic;
namespace KAONOMI.Core.Models;

public class Diary : EntityBase, ISoftDeletable, ITagged
{
    /// <summary>The tags: #TOPIC, !ALERT, ?QUESTION, &amp;LINK, in upper case, no space (<see cref="TagRules"/>).</summary>
    public List<string> Tags { get; set; } = new List<string>();

    public string text { get; set; }    
    public string feeling { get; set; } 
    /// <summary>Soft delete: true once the entry is deleted (DELETE /diary/{id}); it is kept but no longer listed.</summary>
    public bool isDeleted { get; set; } = false;
}