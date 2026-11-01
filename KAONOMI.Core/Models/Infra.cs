namespace KAONOMI.Core.Models;

/// <summary>
/// An infrastructure diagram of the chat (/infra): the editor of /bpmnai with the machines, devices and services of an
/// installation (an LLM in the cloud, a DGX Spark, PCs and Macs, a Raspberry Pi, cameras, sensors…) joined by links.
/// Stored as a BPMN diagram (its XML, a comment, its version), in an index of its own (<c>infra</c>).
/// </summary>
public class Infra : Bpmn
{
}
