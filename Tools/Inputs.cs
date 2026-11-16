using System.ComponentModel;
using Autodesk.AutoCAD.Geometry;

namespace Civil3dMcp.Tools;

[Description("Ponto 3D no WCS, nas unidades do desenho.")]
public sealed record P3(double X, double Y, double Z = 0)
{
    public Point3d ToPoint() => new(X, Y, Z);
    public Point2d To2d() => new(X, Y);
    public static object Of(Point3d p) => new { x = R(p.X), y = R(p.Y), z = R(p.Z) };
    public static object Of(Point2d p) => new { x = R(p.X), y = R(p.Y) };
    private static double R(double v) => Infrastructure.Fmt.R(v);
}

[Description("Ponto COGO a criar.")]
public sealed record CogoInput(
    [property: Description("Leste (X)")] double X,
    [property: Description("Norte (Y)")] double Y,
    [property: Description("Cota (Z)")] double Z = 0,
    [property: Description("Descrição bruta (raw description)")] string? Description = null,
    [property: Description("Nome do ponto (opcional)")] string? Name = null);
