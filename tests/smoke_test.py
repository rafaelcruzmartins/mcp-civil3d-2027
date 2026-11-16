"""Teste de fumaça do Civil3dMcp: fala JSON-RPC/MCP direto com http://127.0.0.1:5101/mcp.

Uso (Civil 3D aberto com um desenho, de preferência vazio/de teste):
    python -I tests/smoke_test.py            # só leitura + execute_csharp com commit=false
    python -I tests/smoke_test.py --write    # também cria objetos MCP_TESTE_* (cada passo é desfazível com U)
"""
import json
import os
import sys
import urllib.request

sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # console do Windows não é UTF-8 por padrão
URL = f"http://127.0.0.1:{os.environ.get('CIVIL3D_MCP_PORT', '5101')}/mcp"
_id = 0


def rpc(method, params=None):
    global _id
    _id += 1
    body = json.dumps({"jsonrpc": "2.0", "id": _id, "method": method, "params": params or {}}).encode()
    req = urllib.request.Request(URL, body, {
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
        "MCP-Protocol-Version": "2025-06-18",
    })
    with urllib.request.urlopen(req, timeout=300) as r:
        text = r.read().decode("utf-8")
    if text.lstrip().startswith("{"):
        return json.loads(text)
    for line in text.splitlines():  # SSE
        if line.startswith("data:"):
            return json.loads(line[5:].strip())
    raise RuntimeError("Resposta inesperada: " + text[:300])


def call(name, args=None):
    res = rpc("tools/call", {"name": name, "arguments": args or {}})
    if "error" in res:
        return False, res["error"]
    r = res["result"]
    payload = json.loads(r["content"][0]["text"])
    return not r.get("isError", False), payload


passed = failed = 0


def step(title, name, args=None, expect_ok=True, check=None):
    global passed, failed
    ok, payload = call(name, args)
    good = ok == expect_ok and (check is None or check(payload))
    passed += good
    failed += not good
    short = json.dumps(payload.get("result", payload.get("error")) if isinstance(payload, dict) else payload,
                       ensure_ascii=False)[:220]
    print(f"[{'OK ' if good else 'FALHA'}] {title}: {short}")
    return payload


def main():
    write = "--write" in sys.argv
    sfx = __import__("time").strftime("%H%M%S")
    init = rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                              "clientInfo": {"name": "smoke", "version": "1"}})
    print("Servidor:", init["result"]["serverInfo"])
    tools = rpc("tools/list")["result"]["tools"]
    print(f"{len(tools)} ferramentas:", ", ".join(sorted(t["name"] for t in tools)))

    step("status", "get_status")
    step("info do desenho", "get_drawing_info")
    step("superfícies", "list_surfaces")
    step("alinhamentos", "list_alignments")
    step("pontos COGO", "list_cogo_points", {"limit": 5})
    step("grupos de pontos", "list_point_groups")
    step("corredores", "list_corridors")
    step("redes", "list_pipe_networks", {"details": False})
    step("estilos de superfície", "list_styles", {"kind": "surface"})
    step("tipo de estilo inválido -> erro", "list_styles", {"kind": "xyz"}, expect_ok=False)
    step("handle inválido -> erro", "get_entity_properties", {"handle": "ZZZ"}, expect_ok=False)
    step("csharp leitura", "execute_csharp", {"code": "Print(Db.Filename); return Civil.GetSurfaceIds().Count;"})
    step("csharp erro de compilação", "execute_csharp", {"code": "return naoExiste;"}, expect_ok=False,
         check=lambda p: p["error"]["details"][0]["line"] == 1)
    step("csharp rollback (commit=false)", "execute_csharp",
         {"code": "var l = new Line(new Point3d(0,0,0), new Point3d(1,1,0)); return Add(l).Handle.ToString();"})

    if write:
        pl = step("polilinha", "create_polyline",
                  {"points": [{"x": 0, "y": 0}, {"x": 100, "y": 0}, {"x": 200, "y": 50}], "layer": "MCP_TESTE"})
        srf = step("superfície TIN", "create_tin_surface", {
            "name": "MCP_TESTE_SUP_" + sfx,
            "points": [{"x": x, "y": y, "z": 100 + 0.05 * x + 0.02 * y} for x in range(-20, 241, 40) for y in range(-60, 121, 40)],
        })
        step("cota na superfície", "get_surface_elevations",
             {"surface": "MCP_TESTE_SUP_" + sfx, "points": [{"x": 100, "y": 0}, {"x": 9999, "y": 9999}]},
             check=lambda p: abs(p["result"][0]["elevation"] - 105) < 0.01 and p["result"][1].get("elevation") is None)
        al = step("alinhamento da polilinha", "create_alignment_from_polyline",
                  {"polylineHandle": pl["result"]["handle"], "name": "MCP_TESTE_ALN_" + sfx})
        step("estaca/offset", "alignment_station_offset", {"alignment": "MCP_TESTE_ALN_" + sfx, "points": [{"x": 50, "y": 10}]})
        step("ponto na estaca", "alignment_point_at_station", {"alignment": "MCP_TESTE_ALN_" + sfx, "stations": [0, 50]})
        step("perfil de superfície", "create_profile_from_surface", {"alignment": "MCP_TESTE_ALN_" + sfx, "surface": "MCP_TESTE_SUP_" + sfx})
        step("cotas do perfil", "get_profile_elevations",
             {"alignment": "MCP_TESTE_ALN_" + sfx, "profile": f"MCP_TESTE_ALN_{sfx} - MCP_TESTE_SUP_{sfx}", "interval": 50})
        step("vista de perfil", "create_profile_view", {"alignment": "MCP_TESTE_ALN_" + sfx, "insertionPoint": {"x": 0, "y": -300}})
        cp = step("pontos COGO", "create_cogo_points",
                  {"points": [{"x": 10, "y": 10, "z": 101, "description": "MCP_TESTE"}], "pointGroup": "MCP_TESTE_GRP_" + sfx})
        step("lista COGO por descrição", "list_cogo_points", {"descriptionContains": "MCP_TESTE"})
        step("propriedades do alinhamento", "get_entity_properties", {"handle": al["result"]["handle"]})
        print("\nObjetos de teste MCP_TESTE_* criados. Apague-os com U (desfazer) ou delete_entities.")

    print(f"\n{passed} OK, {failed} falha(s)")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
