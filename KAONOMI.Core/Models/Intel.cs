using System.Collections.Generic;
namespace KAONOMI.Core.Models;

public class Intel : EntityBase, ISoftDeletable, ITagged
{
    /// <summary>The tags: #TOPIC, !ALERT, ?QUESTION, &amp;LINK, in upper case, no space (<see cref="TagRules"/>).</summary>
    
    public string name { get; set; } = "";
    public string description { get; set; } = ""; 
    public string categories { get; set; } = ""; // possible values : where, what, who, when, where, why, how, with whom, what else, what next, what if, what then, what now, question, observation, insight, idea, suggestion, recommendation, warning, alert, signal, action, event, opportunity, threat, risk, challenge, problem, solution 
    public List<string> triggers { get; set; } = new List<string>();
    public List<string> relatedEntities { get; set; } = new List<string>();
    public List<string> excludedEntities { get; set; } = new List<string>();
    public List<string> events { get; set; } = new List<string>();
    public string signal { get; set; } = "";
    public List<string> Tags { get; set; } = new List<string>();
    public string instruction { get; set; } = "";
    public List<string> actions { get; set; } = new List<string>();
    public bool isDeleted { get; set; } = false;
    public DateTime startDate { get; set; } = DateTime.Now;
    public DateTime endDate { get; set; } = DateTime.Now;

}