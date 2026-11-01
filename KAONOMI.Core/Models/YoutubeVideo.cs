using System.Collections.Generic;
namespace KAONOMI.Core.Models;

public class YoutubeVideo : EntityBase, ISoftDeletable, ITagged
{
    /// <summary>The tags: #TOPIC, !ALERT, ?QUESTION, &amp;LINK, in upper case, no space (<see cref="TagRules"/>).</summary>
    public List<string> Tags { get; set; } = new List<string>();

    public string videoId { get; set; }
    public string title { get; set; }
    public string description { get; set; }
    public string url { get; set; }
    public bool isDeleted { get; set; } = false;
}