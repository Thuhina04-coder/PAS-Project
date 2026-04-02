namespace PUSL2020.Api.Models;

public class SupervisorResearchArea
{
    public int UserId { get; set; }
    public User? Supervisor { get; set; }

    public int ResearchAreaId { get; set; }
    public ResearchArea? ResearchArea { get; set; }
}
