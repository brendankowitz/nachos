namespace Nachos.DataLayer.SqlServer.Schema;

/// <summary>Facts about the schema this build of Nachos ships.</summary>
public static class SchemaInfo
{
    /// <summary>
    /// The version stamped into <c>dbo.SchemaVersion</c> by the post-deployment script. Bump it, and the literal
    /// in <c>Scripts/Script.PostDeployment.sql</c>, together (a test pins that they agree).
    /// </summary>
    public const int CurrentVersion = 1;
}