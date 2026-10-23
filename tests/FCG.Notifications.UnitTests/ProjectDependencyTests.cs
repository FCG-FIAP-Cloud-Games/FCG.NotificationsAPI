using System.Xml.Linq;

namespace FCG.Notifications.UnitTests;

public class ProjectDependencyTests
{
    private static readonly string[] Forbidden =
        ["FIAP.CloudGames", "FCG.UsersAPI", "FCG.CatalogAPI", "FCG.PaymentsAPI"];

    private static readonly string[] ApplicationExpectedReferences = ["FCG.Notifications.Domain"];

    private static readonly string[] InfrastructureExpectedReferences =
        ["FCG.Notifications.Application", "FCG.Notifications.Domain"];

    private static readonly string[] ApiExpectedReferences =
        ["FCG.Notifications.Application", "FCG.Notifications.Infrastructure"];

    [Fact]
    public void Domain_nao_referencia_outros_projetos() =>
        Assert.Empty(ProjectReferences("FCG.Notifications.Domain"));

    [Fact]
    public void Application_referencia_apenas_Domain() =>
        Assert.Equal(ApplicationExpectedReferences, ProjectReferences("FCG.Notifications.Application"));

    [Fact]
    public void Infrastructure_referencia_apenas_Application_e_Domain() =>
        Assert.Equal(InfrastructureExpectedReferences, ProjectReferences("FCG.Notifications.Infrastructure"));

    [Fact]
    public void Api_referencia_apenas_Application_e_Infrastructure() =>
        Assert.Equal(ApiExpectedReferences, ProjectReferences("FCG.Notifications.Api"));

    [Fact]
    public void Nenhum_projeto_referencia_monolito_ou_outros_servicos()
    {
        var offenders = AllProjects()
            .SelectMany(p => ProjectReferenceNames(p).Concat(PackageReferenceNames(p)))
            .Where(name => Forbidden.Any(f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static string[] ProjectReferences(string project)
    {
        var csproj = Path.Combine(RepoRoot(), "src", project, project + ".csproj");
        return ProjectReferenceNames(csproj).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> ProjectReferenceNames(string csproj) =>
        XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')));

    private static IEnumerable<string> PackageReferenceNames(string csproj) =>
        XDocument.Load(csproj).Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty);

    private static IEnumerable<string> AllProjects()
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.GetFiles(RepoRoot(), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{sep}obj{sep}") && !p.Contains($"{sep}bin{sep}"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("*.sln*").Length == 0)
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Raiz da solução não encontrada.");
    }
}