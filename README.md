# mcp-civil3d-2027: Claude ↔ Civil 3D 2027

Servidor MCP que roda **dentro do Civil 3D 2027** e permite ao Claude (Claude Desktop/chat e Claude Code) ler e
modificar o desenho aberto: superfícies, alinhamentos, perfis, pontos COGO, corredores, redes de tubulação e estilos,
além de `execute_csharp` para qualquer coisa da API .NET do Civil 3D.

- **Instalar:** siga o [Civil3D.md](Civil3D.md). O caminho mais curto é o zip do pacote + `install.ps1`; o guia também cobre a instalação manual e a compilação do código.
- **Modificar o código:** o [CLAUDE.md](CLAUDE.md) explica a arquitetura e as armadilhas já resolvidas.

## Requisitos

Windows x64, **Civil 3D 2027**, Claude Desktop e [uv](https://docs.astral.sh/uv/). Para compilar, também o .NET 10 SDK.

## Uso

Com o Civil 3D aberto, peça ao Claude, por exemplo:
- "Liste as superfícies e alinhamentos do desenho."
- "Qual a cota do terreno EG nos pontos (1000, 2000) e (1050, 2010)?"
- "Crie um alinhamento a partir da polilinha 2A3F e o perfil do terreno sobre a superfície EG."
- "Calcule corte e aterro entre EG e PROJETO."

Toda alteração feita pelo Claude é um grupo de undo: um `U` no Civil 3D desfaz a chamada inteira.
Na linha de comando do Civil 3D, `C3DMCP` mostra o status do servidor e `C3DMCPRESTART` o reinicia.

## Ferramentas

| Área | Ferramentas |
|---|---|
| Geral | `get_status`, `get_drawing_info`, `list_entities`, `get_entity_properties`, `create_polyline`, `delete_entities`, `zoom_extents`, `send_command` |
| Superfícies | `list_surfaces`, `get_surface`, `get_surface_elevations`, `create_tin_surface`, `add_surface_data`, `create_volume_surface`, `rebuild_surface` |
| Alinhamentos e perfis | `list_alignments`, `get_alignment`, `alignment_station_offset`, `alignment_point_at_station`, `create_alignment_from_polyline`, `list_profiles`, `get_profile_elevations`, `create_profile_from_surface`, `create_profile_view` |
| Pontos | `list_cogo_points`, `create_cogo_points`, `list_point_groups` |
| Corredores e redes | `list_corridors`, `rebuild_corridor`, `list_pipe_networks` |
| Estilos | `list_styles` |
| Código | `execute_csharp`: use `commit=false` para testar sem gravar |

## Segurança

O servidor escuta só em `127.0.0.1` e recusa requisições com `Host`/`Origin` não locais. Mesmo assim, `execute_csharp`
executa código dentro do Civil 3D: não exponha a porta (5101) na rede.
