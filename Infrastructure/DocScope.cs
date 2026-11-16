using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Civil3dMcp.Infrastructure;

/// <summary>
/// Lock do documento ativo + transação. Commit no sucesso, abort na exceção. O lock de escrita usa
/// um globalCommandName, então cada chamada vira um grupo de undo (um "U" desfaz a chamada inteira).
/// </summary>
public sealed class DocScope : IDisposable
{
    public Document Doc { get; }
    public Database Db { get; }
    public Editor Ed { get; }
    public Transaction Tr { get; }
    public string Tool { get; }

    private readonly DocumentLock _lock;
    private CivilDocument? _civil;
    private bool _committed;

    /// <summary>Documento Civil 3D. Lança se o desenho não for do Civil 3D.</summary>
    public CivilDocument Civil => _civil ??= CivilDocument.GetCivilDocument(Db)
        ?? throw new InvalidOperationException("O desenho ativo não é um documento do Civil 3D.");

    private DocScope(Document doc, bool write, string tool)
    {
        Doc = doc;
        Db = doc.Database;
        Ed = doc.Editor;
        Tool = tool;
        var cmd = "MCP_" + tool.ToUpperInvariant();
        _lock = write
            ? doc.LockDocument(DocumentLockMode.Write, cmd, cmd, false)
            : doc.LockDocument(DocumentLockMode.Read, null, null, false);
        try { Tr = Db.TransactionManager.StartTransaction(); }
        catch { _lock.Dispose(); throw; }
    }

    public static DocScope Open(bool write, string tool)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument
            ?? throw new InvalidOperationException("Nenhum desenho aberto no Civil 3D.");
        if (write && !string.IsNullOrEmpty(doc.CommandInProgress))
            throw new InvalidOperationException(
                $"Há um comando em andamento no Civil 3D ({doc.CommandInProgress}). Termine ou cancele (Esc) e tente de novo.");
        return new DocScope(doc, write, tool);
    }

    public void Commit()
    {
        Tr.Commit();
        _committed = true;
    }

    public void Dispose()
    {
        try
        {
            if (!_committed && !Tr.IsDisposed) Tr.Abort();
            Tr.Dispose();
        }
        finally
        {
            _lock.Dispose();
        }
    }

    // ---- helpers usados pelas ferramentas

    public T Get<T>(ObjectId id, OpenMode mode = OpenMode.ForRead) where T : DBObject =>
        Tr.GetObject(id, mode, false, true) as T
        ?? throw new ArgumentException($"O objeto {id.Handle} não é do tipo {typeof(T).Name}.");

    public ObjectId IdFromHandle(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) throw new ArgumentException("Handle vazio.");
        long value;
        try { value = Convert.ToInt64(handle.Trim(), 16); }
        catch { throw new ArgumentException($"Handle inválido: '{handle}' (esperado hexadecimal, ex.: \"2A3F\")."); }
        if (!Db.TryGetObjectId(new Handle(value), out var id) || id.IsErased)
            throw new ArgumentException($"Nenhum objeto com handle {handle} no desenho.");
        return id;
    }

    public BlockTableRecord CurrentSpace(OpenMode mode = OpenMode.ForRead) =>
        Get<BlockTableRecord>(Db.CurrentSpaceId, mode);

    public BlockTableRecord ModelSpace(OpenMode mode = OpenMode.ForRead) =>
        Get<BlockTableRecord>(SymbolUtilityServices.GetBlockModelSpaceId(Db), mode);

    public ObjectId AddToCurrentSpace(Entity e, string? layer = null)
    {
        if (!string.IsNullOrWhiteSpace(layer)) e.Layer = EnsureLayer(layer);
        var space = CurrentSpace(OpenMode.ForWrite);
        var id = space.AppendEntity(e);
        Tr.AddNewlyCreatedDBObject(e, true);
        return id;
    }

    public string EnsureLayer(string name)
    {
        var lt = Get<LayerTable>(Db.LayerTableId);
        if (lt.Has(name)) return name;
        SymbolUtilityServices.ValidateSymbolName(name, false);
        lt.UpgradeOpen();
        var rec = new LayerTableRecord { Name = name, LinetypeObjectId = SymbolUtilityServices.GetLinetypeContinuousId(Db) };
        lt.Add(rec);
        Tr.AddNewlyCreatedDBObject(rec, true);
        return name;
    }

    public ObjectId LayerId(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Db.Clayer;
        EnsureLayer(name);
        return Get<LayerTable>(Db.LayerTableId)[name];
    }
}
