# Civil3dMcp: guia para o Claude

Plugin .NET 10 que roda **dentro do `acad.exe`** (Civil 3D 2027, R26.0) e expõe um servidor MCP em
`http://127.0.0.1:5101/mcp`: ferramentas tipadas do Civil 3D + `execute_csharp`. Segue o mesmo desenho do
AcadMcp2027 (Kestrel + `ModelContextProtocol.AspNetCore` 2.2, Streamable HTTP stateless).

## Arquitetura

```
Claude Desktop (stdio) → uvx mcp-proxy → HTTP 127.0.0.1:5101/mcp
  → Kestrel (thread pool) → [McpServerTool] em Tools/*.cs → ToolInvoker (log, tempo, erro → JSON)
  → MainThread.InvokeAsync (SynchronizationContext.Post) → DocScope (LockDocument + Transaction + CivilDocument)
```

- **Dois assemblies:**
  - `Loader/` → `Civil3dMcp.Loader.dll`: é o módulo do bundle (`IExtensionApplication` + comandos `C3DMCP` e `C3DMCPRESTART`). No primeiro `Idle`, carrega o `Civil3dMcp.dll` num **`AssemblyLoadContext` isolado** e chama `Civil3dMcp.Entry.Start()` por reflexão.
  - `Civil3dMcp.dll`: servidor e ferramentas.
- **Por que o isolamento:** o Civil 3D 2027 traz o *Autodesk AutoCAD MCP Server* (`C:\Program Files\Autodesk\ApplicationPlugins\AutoCAD-MCP-Server-2027.bundle`), que carrega `ModelContextProtocol.Core` **0.4-preview** no contexto padrão. Sem o ALC próprio, a falha é `TypeLoadException: ISseEventStreamStore ... Version=0.4.0.0`.
  - O ALC resolve da pasta do bundle só o que existe lá (SDK MCP, `Microsoft.Extensions.AI.Abstractions`, `Civil3dMcp.dll`).
  - AutoCAD, Civil 3D e ASP.NET Core vêm do contexto padrão.
  - **Não** coloque `AssemblyMappings` no `PackageContents.xml`.
- **`DocScope`:**
  - Lock de escrita com `globalCommandName`: cada chamada vira um grupo de undo.
  - Recusa escrita se houver um comando em andamento.
  - `s.Civil` dá o `CivilDocument`.
- **`ToolInvoker`:**
  - Modos: `Read`, `Write`, `WriteOptional`, `Raw`, `Custom`.
  - Retorno: `{ok, tool, durationMs, units, result}`, ou `isError` com `{type, message, errorStatus, details, stackTrace}`.
- **`execute_csharp`:**
  - Usa o **Roslyn 4.10 do próprio AutoCAD**, referenciado com `Private=false`; o AutoCAD **não** traz `CSharp.Scripting`.
  - O código vira o corpo de `__Run()` numa classe derivada de `ScriptGlobals`.
  - Compila fora da thread principal, com cache por SHA-256.
  - O assembly é carregado no **mesmo ALC do plugin**.
  - `#line 1` dá a linha certa nos erros.
  - Com `commit=false`, a transação é abortada.

## Adicionar uma ferramenta

Classe `[McpServerToolType]` em `Tools/`, com método estático `[McpServerTool(Name = "...")]` e `Description` em PT-BR,
retornando `ToolInvoker.Read/Write(...)`. Não há lista de registro (`WithToolsFromAssembly`).
- Use `Info.ByName<T>` para achar objetos Civil por nome (o erro lista os disponíveis).
- Use `Info.StyleId` para estilos: o padrão é o primeiro estilo do desenho.
- Graus, WCS e unidades do desenho.

## Armadilhas já resolvidas

| Sintoma | Solução |
|---|---|
| `TypeLoadException ... ModelContextProtocol.Core 0.4.0.0` | Loader + `AssemblyLoadContext` isolado (veja acima) |
| `Path`/`File` não encontrados | `UseWPF`/`UseWindowsForms` tiram `System.IO` dos usings implícitos: `<Using Include="System.IO" />` |
| MSB3277 (System.Drawing, WindowsBase) | `UseWPF` + `UseWindowsForms` no csproj (no Loader também) |
| `DBObject` ambíguo | Existe `Autodesk.Civil.DatabaseServices.DBObject`: qualifique o do AutoCAD |
| `TinSurfaceProperties.Area2D` não existe | Propriedades lidas por reflexão (`Info.PlainProperties`) |
| `StylesRoot.SubassemblyStyles` não existe | Removido do `list_styles` |

## Fluxo de trabalho

1. **Deploy:** feche o Civil 3D e rode `build-and-deploy.ps1`. Ele instala em `%APPDATA%\Autodesk\ApplicationPlugins\Civil3dMcp.bundle`.
2. **Teste:** abra o Civil 3D com um desenho de teste e rode `python -I tests\smoke_test.py`. Com `--write`, o teste também cria objetos `MCP_TESTE_*`. Os logs ficam em `%APPDATA%\Civil3dMcp\logs\`.
3. **Claude Desktop:** a entrada `civil3d` fica no `claude_desktop_config.json`, cujo local varia com o tipo de instalação (tabela em `Civil3D.md`). A ponte é `uvx --with "mcp<2" mcp-proxy --transport streamablehttp http://127.0.0.1:5101/mcp`.
4. **Distribuir para outras contas/máquinas:** `package.ps1` gera `dist\Civil3dMcp-<versão>.zip` (bundle + `install.ps1` + `Civil3D.md`); quem recebe roda `install.ps1`, sem precisar do SDK. O guia completo está em `Civil3D.md`.
5. **Porta:** a variável `CIVIL3D_MCP_PORT` muda a porta. A porta 5100 fica reservada para o AcadMcp.
