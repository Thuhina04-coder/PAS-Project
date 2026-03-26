namespace PUSL2020.Api.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    public List<Project> Projects { get; set; } = new();
    public List<SupervisorResearchArea> SupervisorResearchAreas { get; set; } = new();
}
