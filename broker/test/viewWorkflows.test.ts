import test from "node:test";
import assert from "node:assert/strict";
import { Client, InMemoryTransport } from "@modelcontextprotocol/client";
import { createBrokerServer } from "../src/server.js";
import { FakeRevitBridgeClient } from "../src/ipc/FakeRevitBridgeClient.js";

test("documentation tools require exact document targeting and expose bounded typed operations", async () => {
  const server = createBrokerServer({bridge:new FakeRevitBridgeClient(), brokerVersion:"test", sessionId:"view-contract"});
  const client = new Client({name:"view-contract", version:"1"});
  const [ct, st] = InMemoryTransport.createLinkedPair();
  await server.connect(st); await client.connect(ct);
  try {
    const {tools} = await client.listTools();
    for (const name of ["get_view_details", "get_dimensions", "activate_view"]) {
      const tool = tools.find(t => t.name === `revit.${name}`)!;
      assert.ok(tool);
      assert.deepEqual(tool.inputSchema.required, ["instanceId", "documentFingerprint"]);
      assert.equal(tool.annotations?.readOnlyHint, name !== "activate_view");
      const missing = await client.callTool({name:tool.name, arguments:{viewId:"1024"}});
      assert.equal(missing.isError, true);
    }
    const point = {x:{value:0,unit:"mm"}, y:{value:0,unit:"mm"}, z:{value:0,unit:"mm"}};
    const operations = [
      {type:"create_plan_view", levelId:"1", viewFamilyTypeId:"2", name:"OKF plan"},
      {type:"duplicate_view", viewId:"1024", name:"Detailing copy"},
      {type:"duplicate_sheet", sheetId:"1", sheetNumber:"A-2", viewNamePrefix:"Copy "},
      {type:"copy_view_annotations", sourceViewId:"1", targetViewId:"2", elementIds:["3"]},
      {type:"create_dimension", viewId:"1", dimensionTypeId:"2", references:["a", "b"], start:point, end:{...point,x:{value:1000,unit:"mm"}}},
      {type:"update_dimension", elementId:"1", text:[{above:"VERIFY"}]},
    ];
    for (const operation of operations) {
      const result = await client.callTool({name:"revit.preview_change_set", arguments:{operations:[operation]}});
      assert.notEqual(result.isError, true, `${operation.type} must pass the MCP schema`);
      const data = (result.structuredContent as {data:{ready:boolean;changes:Array<{message?:string}>}}).data;
      assert.equal(data.ready, false, "Fake mode must not claim native validation");
      assert.match(data.changes[0]?.message ?? "", /Native Revit host required/);
    }
    const invalid = await client.callTool({name:"revit.preview_change_set", arguments:{operations:[{type:"copy_view_annotations",sourceViewId:"1",targetViewId:"2",elementIds:[]}]}});
    assert.equal(invalid.isError, true);
  } finally { await client.close(); await server.close(); }
});
