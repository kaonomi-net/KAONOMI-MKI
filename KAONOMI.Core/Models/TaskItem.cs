using System.Collections.Generic;
namespace KAONOMI.Core.Models;

public class TaskItem : EntityBase, ISoftDeletable, ITagged
{

    public string name { get; set; }    
    public string description { get; set; }
    public DateTime startTime { get; set; }
    public DateTime endTime { get; set; }
    public string securityConstraints { get; set; }
    public string legalConstraints { get; set; }
    public string dataConstraints { get; set; }
    public string type { get; set; } 
    public string status {  get; set; } // backlog, todo, coding, test, check, deploy, archived
    public bool isDeleted { get; set; } = false;
}