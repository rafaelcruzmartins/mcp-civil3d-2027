# Civil3dMcp: instalação e guia para o Claude

Plugin .NET 10 que roda **dentro do `acad.exe`** (Civil 3D 2027, R26.0) e expõe um servidor MCP em
`http://127.0.0.1:5101/mcp`: 32 ferramentas tipadas do Civil 3D e `execute_csharp`. Com ele, o Claude (Claude
Desktop/chat e Claude Code) lê e modifica o desenho aberto.

Este arquivo serve para **instalar em outra conta ou máquina**: explica o que instalar, onde, como verificar e o
que já deu errado. Se você é o Claude ajudando alguém a instalar, siga a seção *Instalação* na ordem e confirme
cada verificação antes de passar para a próxima.

## Como funciona (o fluxo de uma chamada)

```
Claude Desktop ──stdio──> uvx mcp-proxy ──HTTP──> 127.0.0.1:5101/mcp (Kestrel dentro do acad.exe)
  → [McpServerTool] em Tools/*.cs → ToolInvoker (log, tempo, erro → JSON)
  → MainThread.InvokeAsync (thread do AutoCAD) → DocScope (LockDocument + Transaction + CivilDocument)
```

- **O que fica onde:**
  - **Plugin:** fica no Civil 3D, como bundle em `ApplicationPlugins`, e carrega sozinho ao abrir o programa.
  - **Ponte:** o Claude Desktop só aceita servidores stdio, então o `mcp-proxy` (via `uvx`) converte stdio em HTTP.
  - **Claude Code:** fala HTTP direto e não precisa da ponte.
- **Escopo:** o plugin e a configuração valem **por usuário do Windows**. Qualquer conta Claude logada nesse usuário usa a mesma configuração do Claude Desktop. Para outro usuário do Windows, instale de novo nele ou use `-AllUsers`.
- **Segurança:** o servidor só aceita conexões locais (loopback, com checagem de `Host`/`Origin`). Mesmo assim, `execute_csharp` executa código dentro do Civil 3D: não exponha a porta na rede.

## Pré-requisitos

| Item | Como conferir / instalar |
|---|---|
| Windows 10/11 x64 | — |
| **Civil 3D 2027** | Deve existir `C:\Program Files\Autodesk\AutoCAD 2027\C3D\AeccDbMgd.dll`. Outras versões (2025/2026) **não** funcionam sem recompilar. |
| **Claude Desktop** | Instalado e logado. |
| **uv** (fornece o `uvx`) | `winget install --id astral-sh.uv -e`. Confira com `uvx --version`; depois de instalar, abra um terminal novo. |
| .NET 10 SDK | **Só para compilar** do código-fonte (opção C). O pacote pronto não precisa. O runtime .NET 10 já vem com o Civil 3D 2027. |

## Instalação

### A. Pacote pronto (recomendado)

1. Baixe o `Civil3dMcp-<versão>.zip` em *Releases* do repositório (ou receba de quem gerou com `package.ps1`) e descompacte, por exemplo em `C:\Civil3dMcp`. Sem zip, use a opção C.
2. **Feche o Civil 3D.**
3. No PowerShell, dentro da pasta descompactada:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
   O script:
   1. copia `Civil3dMcp.bundle` para `%APPDATA%\Autodesk\ApplicationPlugins\` (e desbloqueia os arquivos do zip);
   2. localiza o `uvx` e baixa a ponte `mcp-proxy` para o cache;
   3. localiza o `claude_desktop_config.json`, faz backup (`.bak-AAAAMMDD-HHMMSS`) e adiciona o servidor `civil3d`.

   Opções:
   - `-ConfigPath <arquivo>`: use se o script achar zero ou mais de um arquivo de configuração.
   - `-AllUsers`: instala o plugin em `C:\ProgramData\...`, para todos os usuários do Windows. Precisa de admin.
   - `-SkipConfig`: instala só o plugin.
   - `-Port <n>`: troca a porta.
4. **Reinicie o Claude Desktop.** Não basta fechar a janela: saia pelo ícone da bandeja (*Sair*) e abra de novo.
5. Siga a *Verificação*.

### B. Manual (sem script)

1. **Plugin:** copie a pasta `Civil3dMcp.bundle` inteira para `%APPDATA%\Autodesk\ApplicationPlugins\`, com o Civil 3D fechado. Se ela veio de um zip baixado, desbloqueie os arquivos: botão direito > Propriedades > Desbloquear, ou `Get-ChildItem -Recurse | Unblock-File`.
2. **Configuração do Claude Desktop:** abra *Configurações > Desenvolvedor > Editar configuração*. Isso abre o arquivo certo; veja abaixo onde ele pode estar. Dentro de `"mcpServers"`, acrescente a entrada abaixo, trocando `SEU_USUARIO`:
   ```json
   "civil3d": {
     "command": "C:\\Users\\SEU_USUARIO\\.local\\bin\\uvx.exe",
     "args": ["--with", "mcp<2", "mcp-proxy", "--transport", "streamablehttp", "http://127.0.0.1:5101/mcp"]
   }
   ```
   - Use o **caminho completo** do `uvx.exe`; para descobrir, rode `(Get-Command uvx).Source`. O Claude Desktop não expande `%USERPROFILE%` nem herda o PATH do terminal.
   - Barras invertidas são **duplas** no JSON. Mantenha as vírgulas entre servidores.
   - `mcp<2` é **obrigatório**: o `mcp-proxy` atual quebra com `mcp` 2.x.
3. Rode uma vez `uvx --with "mcp<2" mcp-proxy --help`. Isso enche o cache; sem ele, a primeira inicialização pode estourar o tempo limite do Claude Desktop.
4. Reinicie o Claude Desktop pela bandeja.

**Onde fica o `claude_desktop_config.json`**, conforme o tipo de instalação do Claude Desktop:

| Instalação | Caminho |
|---|---|
| Dados fora do pacote (a deste projeto) | `%LOCALAPPDATA%\Claude-Data\claude_<id>\claude_desktop_config.json` |
| Microsoft Store / MSIX | `%LOCALAPPDATA%\Packages\Claude_<id>\LocalCache\Roaming\Claude\claude_desktop_config.json` |
| Instalador clássico | `%APPDATA%\Claude\claude_desktop_config.json` |

Na dúvida, use *Editar configuração* no próprio app.

### C. Compilar do código-fonte

Precisa do .NET 10 SDK (`winget install --id Microsoft.DotNet.SDK.10`) e do Civil 3D 2027 instalado, porque as DLLs do
AutoCAD e do Civil 3D são referenciadas de `C:\Program Files\Autodesk\AutoCAD 2027`. Se o caminho for outro, passe `-p:AcadDir=...`.

```powershell
git clone https://github.com/rafaelcruzmartins/mcp-civil3d-2027.git
cd mcp-civil3d-2027
.\package.ps1                                            # compila plugin + Loader e gera dist\Civil3dMcp-<versão>.zip
powershell -ExecutionPolicy Bypass -File .\install.ps1   # Civil 3D fechado: instala o plugin e configura o Claude Desktop
```

`install.ps1` acha o plugin compilado em `dist\Civil3dMcp`. Para iterar no código, `build-and-deploy.ps1` compila e
reinstala só o plugin (sem mexer na configuração do Claude Desktop).

### Claude Code (opcional)

O Claude Code aceita HTTP direto, sem ponte:

```powershell
claude mcp add --transport http --scope user civil3d http://127.0.0.1:5101/mcp
```

## Verificação

1. Abra o **Civil 3D 2027**. Na linha de comando deve aparecer `Civil3dMcp: servidor MCP em http://127.0.0.1:5101/mcp`.
2. Digite `C3DMCP` na linha de comando: deve responder `Civil3dMcp ativo em ...`.
3. No navegador, `http://127.0.0.1:5101/` deve mostrar `Civil3dMcp ativo`.
4. No Claude Desktop, o conector `civil3d` deve aparecer na lista de ferramentas do chat. Peça: *"Use get_status do Civil 3D"*.
5. Teste completo, opcional (precisa de Python): `python -I tests\smoke_test.py` faz só leitura e deve dar 14 OK. `--write` cria objetos `MCP_TESTE_*` e deve dar 26 OK; rode só num desenho de teste.

## Uso

- **Pedidos de exemplo:**
  - "Liste as superfícies e alinhamentos."
  - "Qual a cota da superfície EG em (1000, 2000)?"
  - "Crie um alinhamento a partir da polilinha 2A3F e o perfil do terreno."
  - "Calcule corte e aterro entre EG e PROJETO."
- **Undo:** cada alteração feita pelo Claude é **um grupo de undo**; um `U` no Civil 3D desfaz a chamada inteira.
- **Unidades:** ângulos em graus, coordenadas no WCS, nas unidades do desenho.
- **`execute_csharp`:** cobre o que não tiver ferramenta. O padrão é `commit=false`, que testa sem gravar nada.

| Área | Ferramentas |
|---|---|
| Geral | `get_status`, `get_drawing_info`, `list_entities`, `get_entity_properties`, `create_polyline`, `delete_entities`, `zoom_extents`, `send_command` |
| Superfícies | `list_surfaces`, `get_surface`, `get_surface_elevations`, `create_tin_surface`, `add_surface_data`, `create_volume_surface`, `rebuild_surface` |
| Alinhamentos e perfis | `list_alignments`, `get_alignment`, `alignment_station_offset`, `alignment_point_at_station`, `create_alignment_from_polyline`, `list_profiles`, `get_profile_elevations`, `create_profile_from_surface`, `create_profile_view` |
| Pontos | `list_cogo_points`, `create_cogo_points`, `list_point_groups` |
| Corredores e redes | `list_corridors`, `rebuild_corridor`, `list_pipe_networks` |
| Estilos | `list_styles` |
| Código | `execute_csharp` (globais: `Doc`, `Db`, `Ed`, `Tr`, `Civil`, `Print`, `Id`, `Open<T>`, `ModelSpace`, `CurrentSpace`, `Add`, `Rad`, `Deg`) |

## Problemas comuns

| Sintoma | Causa / solução |
|---|---|
| O conector `civil3d` não aparece no Claude Desktop | O app não foi reiniciado pela bandeja, o JSON está inválido (falta vírgula, barra simples) ou o caminho do `uvx.exe` está errado. Veja os logs em *Configurações > Desenvolvedor*. |
| Erro "cannot connect" / conexão recusada | O Civil 3D não está aberto, ou o plugin não carregou: rode `C3DMCP`. Se o servidor estiver parado, use `C3DMCPRESTART` e veja o log. |
| `C3DMCP` é "comando desconhecido" | O bundle não está em `ApplicationPlugins`, os arquivos estão bloqueados (veio de zip: use `Unblock-File`) ou não é o Civil 3D **2027**. Confira com `APPLOAD` > *Startup Suite* / `NETLOAD` manual do `Civil3dMcp.Loader.dll`. |
| Log com `TypeLoadException ... ModelContextProtocol.Core 0.4.0.0` | Alguém removeu o Loader ou carregou o `Civil3dMcp.dll` direto. O módulo do bundle tem que ser o `Civil3dMcp.Loader.dll` (veja *Armadilhas*). |
| Timeout nas ferramentas | O Civil 3D está ocupado, com um comando ativo ou com uma caixa de diálogo aberta. Pressione Esc no Civil 3D. |
| "Há um comando em andamento" | Termine ou cancele o comando no Civil 3D antes de pedir alterações. |
| Porta 5101 ocupada | Defina a variável de usuário `CIVIL3D_MCP_PORT` (ou `install.ps1 -Port`) e ajuste a URL na configuração. |
| Atualizar o plugin | Feche o Civil 3D e rode `install.ps1` (pacote novo) ou `build-and-deploy.ps1` (código-fonte). |

**Logs:** `%APPDATA%\Civil3dMcp\logs\civil3dmcp-AAAAMMDD.log`. Cada chamada registra a ferramenta, o tempo e o erro, se houver.

## Arquitetura do código (para quem for modificar)

- **`Loader/LoaderPlugin.cs` → `Civil3dMcp.Loader.dll`:**
  - É o **módulo do bundle**: `IExtensionApplication` e os comandos `C3DMCP` e `C3DMCPRESTART`.
  - No primeiro `Application.Idle`, cria um `AssemblyLoadContext` próprio, carrega o `Civil3dMcp.dll` nele e chama `Civil3dMcp.Entry.Start()` por reflexão.
- **`Plugin.cs` (`Entry`):**
  - Captura o `SynchronizationContext` da thread principal.
  - Sobe o Kestrel e aquece o Roslyn em background.
- **`Infrastructure/`:**
  - `MainThread`: despacha trabalho para a thread do AutoCAD, com timeout. Trabalho que não começou a tempo é cancelado e nunca roda atrasado.
  - `DocScope`: lock e transação. Commit no sucesso, abort na exceção. O lock de escrita usa `globalCommandName`, o que faz de cada chamada um grupo de undo. Recusa escrita se houver comando em andamento.
  - `Fmt`: opções JSON. Chama-se `Fmt` porque existe um namespace global `Json`.
  - `Log`: log em arquivo.
- **`Server/`:**
  - `McpHost`: Kestrel só em loopback, `LoopbackGuard`, lifetime e logging sem console.
  - `ToolInvoker`: modos `Read`, `Write`, `WriteOptional`, `Raw` e `Custom`.
- **`Tools/`:** uma classe `[McpServerToolType]` por área. Registro automático por `WithToolsFromAssembly`; não há lista para manter.
- **`Scripting/CSharpRunner.cs`:**
  - Compila com o **Roslyn 4.10 do próprio AutoCAD** (`CSharpCompilation`; o AutoCAD não traz `CSharp.Scripting`).
  - O código vira o corpo de `__Run()` numa classe derivada de `ScriptGlobals`.
  - Cache por SHA-256. O assembly é carregado no ALC do plugin.

### Adicionar uma ferramenta

```csharp
[McpServerTool(Name = "minha_tool"), Description("O que faz, em PT-BR, com unidades e formatos.")]
public static Task<CallToolResult> MinhaTool([Description("Nome do alinhamento.")] string alignment) =>
    ToolInvoker.Read("minha_tool", new { alignment }, s =>      // Write para alterar o desenho
    {
        var a = AlignmentTools.Find(s, alignment);                // erro já lista os nomes disponíveis
        return new { a.Name, length = Fmt.R(a.Length) };
    });
```

- Use `Info.ByName<T>` para achar objetos pelo nome e `Info.StyleId` para estilos (o padrão é o primeiro do desenho).
- Erros de validação: lance `ArgumentException` ou `ToolException(msg, details)`; eles vão sem stack trace.

## Armadilhas já resolvidas (não repita)

| Sintoma | Causa | Solução |
|---|---|---|
| `TypeLoadException: ISseEventStreamStore ... ModelContextProtocol.Core, Version=0.4.0.0` | O Civil 3D 2027 traz o *Autodesk AutoCAD MCP Server* (preview), que carrega o SDK MCP 0.4 no contexto padrão | Loader + `AssemblyLoadContext` isolado. **Não** use `AssemblyMappings` no `PackageContents.xml`. |
| Kestrel ia precisar de um resolvedor de assemblies | O `acdbmgd.runtimeconfig.json` já declara `Microsoft.AspNetCore.App` | Basta `FrameworkReference` |
| `Path`/`File` não encontrados | `UseWPF`/`UseWindowsForms` tiram `System.IO` dos usings implícitos | `<Using Include="System.IO" />` |
| MSB3277 (System.Drawing, WindowsBase) | As DLLs do AutoCAD compilam contra o WindowsDesktop | `UseWPF` + `UseWindowsForms` (nos dois csproj) |
| `DBObject` ambíguo | Existe `Autodesk.Civil.DatabaseServices.DBObject` | Qualifique o do AutoCAD |
| `TinSurfaceProperties.Area2D` e `StylesRoot.SubassemblyStyles` não existem | A API do 2027 difere da documentação antiga | Ler por reflexão (`Info.PlainProperties`) / remover |
| Schema com `NaN/Infinity` / `TypeInfoResolver read-only` | O SDK faz `MakeReadOnly()` nas opções | `Fmt.Options` (entrada) separado de `OutputOptions` (saída), com `DefaultJsonTypeInfoResolver` |
| Claude Desktop não inicia a ponte | O `mcp-proxy` atual quebra com `mcp` 2.x; caminho relativo do `uvx` | `--with "mcp<2"` e caminho absoluto do `uvx.exe` |
| `.ps1` com acentos quebra | PowerShell 5.1 lê UTF-8 sem BOM como ANSI | Scripts em ASCII ou UTF-8 **com BOM** |

## Desinstalar

1. Feche o Civil 3D e apague `%APPDATA%\Autodesk\ApplicationPlugins\Civil3dMcp.bundle` (ou o de `C:\ProgramData\...`).
2. Remova a entrada `"civil3d"` do `claude_desktop_config.json` (há backups `.bak-*` ao lado) e reinicie o Claude Desktop.
3. Opcional: apague `%APPDATA%\Civil3dMcp` (logs) e a variável `CIVIL3D_MCP_PORT`.
