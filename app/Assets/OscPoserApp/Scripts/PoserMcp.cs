// MCP(Model Context Protocol)のサーバー(0.4.0)。-api PORT の口の POST /mcp で、JSON-RPC 2.0 を受ける(Streamable HTTP。
// 答えはいつも application/json で、SSE は使わない)。AI(Claude など)が OscPoser をツールとして使える。
//   例: claude mcp add --transport http oscposer http://127.0.0.1:9330/mcp
// ツールは、/cmd のコマンド(PoserApi.cs の HandleCmd)を包んだもの。screenshot は画面を画像で返す。
// ブラウザからの要求は ApiServe が断る(Origin・Host を見る)。
using System;
using System.Collections.Generic;
using System.Linq;

public partial class PoserApp
{
    static readonly string[] McpVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    // ネットワークのスレッドから呼ぶ。返すのは (HTTP のコード, 本文)
    (int, string) McpHandle(string body)
    {
        object msg;
        try { msg = MiniJson.Parse(body); }
        catch (Exception e) { return (200, Json(RpcError(null, -32700, "parse error: " + e.Message))); }
        if (msg is List<object> batch)
        {
            var outs = batch.OfType<Dictionary<string, object>>().Select(McpOne).Where(r => r != null).Cast<object>().ToList();
            return outs.Count == 0 ? (202, "") : (200, Json(outs));
        }
        if (!(msg is Dictionary<string, object> one)) return (200, Json(RpcError(null, -32600, "invalid request")));
        var res = McpOne(one);
        return res == null ? (202, "") : (200, Json(res));
    }

    static Dictionary<string, object> RpcError(object id, int code, string message) => new Dictionary<string, object>
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = message },
    };

    static Dictionary<string, object> RpcResult(object id, object result) =>
        new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    Dictionary<string, object> McpOne(Dictionary<string, object> m)
    {
        bool isRequest = m.ContainsKey("id");
        m.TryGetValue("id", out var id);
        var method = S(m, "method") ?? "";
        var prm = m.TryGetValue("params", out var po) ? po as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
        if (!isRequest) return null;   // 知らせ(notifications/initialized など)は 202 で受けるだけ
        switch (method)
        {
            case "initialize":
            {
                var want = S(prm, "protocolVersion");
                return RpcResult(id, new Dictionary<string, object>
                {
                    ["protocolVersion"] = McpVersions.Contains(want) ? want : McpVersions[0],
                    ["capabilities"] = new Dictionary<string, object> { ["tools"] = new Dictionary<string, object>() },
                    ["serverInfo"] = new Dictionary<string, object> { ["name"] = "OscPoser", ["version"] = Version },
                    ["instructions"] = "OscPoser shows a VRM avatar in a pose (from a humanoid .anim at a time, or a pose .json) and sends that pose to VRChat over OSC "
                        + "(the avatar needs the OscPoser prefab). Opening a pose does not send it: call send_pose, then the pose turns ON. "
                        + "Use screenshot to see the avatar. 'port' selects the VRChat OSC input port (9000, 9010, 9020...); omitted = the port shown in the window.",
                });
            }
            case "ping":
                return RpcResult(id, new Dictionary<string, object>());
            case "tools/list":
                return RpcResult(id, new Dictionary<string, object> { ["tools"] = McpTools() });
            case "tools/call":
                return RpcResult(id, McpCall(S(prm, "name") ?? "",
                    prm.TryGetValue("arguments", out var ao) ? ao as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>()));
            default:
                return RpcError(id, -32601, "method not found: " + method);
        }
    }

    // ---- ツール ----

    static Dictionary<string, object> P(string type, string desc, Dictionary<string, object> extra = null)
    {
        var d = new Dictionary<string, object> { ["type"] = type, ["description"] = desc };
        if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
        return d;
    }

    static readonly Dictionary<string, object> PortProp = P("integer", "VRChat OSC input port of the target (9000, 9010, 9020...). Omit for the port shown in the window. Another port works only when OscPoser was started with -targets; otherwise use set_port first.");

    static Dictionary<string, object> Tool(string name, string desc, Dictionary<string, object> props, params string[] required)
    {
        props = new Dictionary<string, object>(props) { ["port"] = PortProp };
        var schema = new Dictionary<string, object> { ["type"] = "object", ["properties"] = props };
        if (required.Length > 0) schema["required"] = required.Cast<object>().ToList();
        return new Dictionary<string, object> { ["name"] = name, ["description"] = desc, ["inputSchema"] = schema };
    }

    static Dictionary<string, object> Props(params (string, Dictionary<string, object>)[] ps) => ps.ToDictionary(p => p.Item1, p => (object)p.Item2);

    List<object> McpTools()
    {
        var num = new Func<string, Dictionary<string, object>>(d => P("number", d));
        return new List<object>
        {
            Tool("get_state", "Current state: version, targets (port, VRM, animation/pose file, time, pose ON, sending progress, holding), body offsets, status line.", Props()),
            Tool("set_port", "Switch the window's port (same as typing it in the Port field). If OscPoser was started with -targets, each port keeps its own VRM, pose, hands and sending; otherwise the VRM and pose stay and only the destination changes.",
                Props(), "port"),
            Tool("open_vrm", "Load a VRM (0.x or 1.x) for this target. Use the same model as the VRChat avatar.",
                Props(("path", P("string", "Absolute path of the .vrm file"))), "path"),
            Tool("open_animation", "Open a humanoid .anim (Unity text format) and pick a time. Does not send. A partial clip (hands only, etc.) is layered on the current pose.",
                Props(("path", P("string", "Absolute path of the .anim (or a pose .json)")), ("time", num("Seconds into the clip (default: the end)")),
                      ("frame", num("Frame number instead of time"))), "path"),
            Tool("open_pose", "Open a pose .json (muscle name -> value in Unity muscle units, plus \"(RootT.y)\" etc.), or give the values directly. Partial values are layered on the current pose. Does not send.",
                Props(("path", P("string", "Absolute path of the pose .json")), ("values", P("object", "Muscle name -> value (e.g. {\"Right Hand Down-Up\": 0.5})")))),
            Tool("set_time", "Move to a time in the open animation.", Props(("time", num("Seconds")), ("frame", num("Frame number")))),
            Tool("send_pose", "Send the current pose to VRChat and turn the pose ON (only changed values unless all=true; takes a few to ~15 seconds in the background).",
                Props(("all", P("boolean", "Resend every value (after the avatar was reloaded, or for people who joined later)")))),
            Tool("set_pose_on", "Turn the pose ON or OFF in VRChat (OFF returns to normal so the avatar can walk).", Props(("on", P("boolean", "true = ON"))), "on"),
            Tool("set_body", "Body position/rotation added on top of the pose. y/x/z: -1..1 (= ±1.5 humanoid units; up, right, forward). yaw/pitch/roll: degrees.",
                Props(("y", num("up/down")), ("x", num("right/left")), ("z", num("forward/back")), ("yaw", num("turn, degrees")), ("pitch", num("lean forward, degrees")),
                      ("roll", num("lean sideways, degrees")),
                      ("mode", P("string", "offset = set the added amount (default), add = add to it, abs = make pose+offset equal to the value", new Dictionary<string, object> { ["enum"] = new List<object> { "offset", "add", "abs" } })),
                      ("reset", P("boolean", "Clear all offsets first")), ("send", P("boolean", "Send after changing")))),
            Tool("move_hand", "Move one hand by IK (arm = wrist position + hand rotation + elbow swing). Does not send by itself.",
                Props(("side", P("string", "R or L", new Dictionary<string, object> { ["enum"] = new List<object> { "R", "L" } })),
                      ("move", P("array", "[right, up, forward] in cm (scaled to the avatar size)", new Dictionary<string, object> { ["items"] = new Dictionary<string, object> { ["type"] = "number" } })),
                      ("rot", P("array", "Wrist turns in the hand bone's axes: [[\"x\", deg], ...]. x = twist, z = bend palm/back, y = bend sideways",
                          new Dictionary<string, object> { ["items"] = new Dictionary<string, object> { ["type"] = "array" } })),
                      ("elbow", num("Swing the elbow: + away from the body, - toward it (degrees)")),
                      ("grip", P("boolean", "Toggle fingers: light grip / from the pose")), ("reset", P("boolean", "Put this hand back to the pose"))), "side"),
            Tool("get_pose_values", "Current pose values (same form as a pose .json).", Props()),
            Tool("save_pose", "Save the current pose as a pose .json.", Props(("path", P("string", "Absolute path ending in .json"))), "path"),
            Tool("held_object", "Held world object (VRChat OSC input; desktop holds with the right hand only). grab = bring VRChat to front and keep holding, release, spin = turn it (about 4.6° per step).",
                Props(("action", P("string", "grab, release or spin", new Dictionary<string, object> { ["enum"] = new List<object> { "grab", "release", "spin" } })),
                      ("axis", P("string", "for spin", new Dictionary<string, object> { ["enum"] = HoldInput.Axes.Cast<object>().ToList() })),
                      ("sign", num("for spin: 1 or -1")), ("count", num("for spin: steps"))), "action"),
            Tool("screenshot", "Capture the OscPoser window (the avatar in the current pose) and return it as an image.",
                Props(("view", P("string", "Reset the camera first", new Dictionary<string, object> { ["enum"] = new List<object> { "front", "right", "left", "back" } })))),
            Tool("stop_sending", "Cancel values not sent yet, for all targets (cycle sending continues).", Props()),
        };
    }

    Dictionary<string, object> McpCall(string name, Dictionary<string, object> a)
    {
        var req = new Dictionary<string, object>(a);
        string cmd;
        switch (name)
        {
            case "get_state": cmd = "state"; break;
            case "set_port": cmd = "target"; break;
            case "open_vrm": cmd = "vrm"; break;
            case "open_animation": cmd = "anim"; break;
            case "open_pose": cmd = "pose"; break;
            case "set_time": cmd = "time"; break;
            case "send_pose": cmd = "send"; break;
            case "set_pose_on": cmd = Bq(a, "on") ? "on" : "off"; req.Remove("on"); break;
            case "set_body": cmd = "body"; break;
            case "move_hand": cmd = "ik"; break;
            case "get_pose_values": cmd = "values"; break;
            case "save_pose": cmd = "save"; break;
            case "held_object": cmd = S(a, "action") ?? ""; req.Remove("action"); if (cmd != "grab" && cmd != "release" && cmd != "spin") cmd = "?"; break;
            case "screenshot": cmd = "shot"; req.Remove("path"); break;
            case "stop_sending": cmd = "stop"; break;
            default:
                return ToolResult(new List<object> { Text("unknown tool: " + name) }, true);
        }
        req["cmd"] = cmd;
        var res = RunOnMain(req);
        bool ok = res.TryGetValue("ok", out var o) && o is bool b && b;
        var content = new List<object>();
        if (res.TryGetValue("png", out var png) && png is string b64)
        {
            content.Add(new Dictionary<string, object> { ["type"] = "image", ["data"] = b64, ["mimeType"] = "image/png" });
            res.Remove("png");
        }
        res.Remove("ok");
        content.Add(Text(Json(res)));
        return ToolResult(content, !ok);
    }

    static Dictionary<string, object> Text(string t) => new Dictionary<string, object> { ["type"] = "text", ["text"] = t };
    static Dictionary<string, object> ToolResult(List<object> content, bool error) => new Dictionary<string, object> { ["content"] = content, ["isError"] = error };
}
