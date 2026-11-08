using System.Collections.Generic;
namespace KAONOMI.Core.Models;

public class YoutubeVideo : EntityBase, ISoftDeletable, ITagged
{

    public string videoId { get; set; }
    public string description { get; set; }
    public string url { get; set; }
    public bool isDeleted { get; set; } = false;
}