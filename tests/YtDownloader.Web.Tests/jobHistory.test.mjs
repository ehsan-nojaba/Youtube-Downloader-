import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = await readFile(new URL("../../src/YtDownloader.Web/wwwroot/js/jobHistory.js", import.meta.url), "utf8");
const history = await import("data:text/javascript;base64," + Buffer.from(source).toString("base64"));
let stored = new Map();
globalThis.localStorage = {
    getItem: key => stored.get(key) ?? null,
    setItem: (key, value) => stored.set(key, value)
};
const key = "YtDownloader.jobIds";
const id = number => "12345678-1234-1234-1234-" + String(number).padStart(12, "0");

test("saves only distinct IDs, newest first, with a twenty-job limit", () => {
    stored = new Map();
    for (let index = 1; index <= 25; index++) history.addId(id(index));
    history.addId(id(24));
    assert.equal(history.getIds().length, 20);
    assert.equal(history.getIds()[0], id(24));
    assert.equal(history.getIds()[1], id(25));
    assert.equal(new Set(history.getIds()).size, 20);
    assert.deepEqual(JSON.parse(stored.get(key)), history.getIds());
});

test("ignores corrupt storage, invalid IDs, and the empty GUID", () => {
    stored = new Map([[key, "broken json"]]);
    assert.deepEqual(history.getIds(), []);
    stored.set(key, JSON.stringify([id(1), "bad", null, "00000000-0000-0000-0000-000000000000", id(1)]));
    assert.deepEqual(history.getIds(), [id(1)]);
});

test("separate browser storage does not expose another browser's history", () => {
    stored = new Map();
    history.addId(id(1));
    const firstBrowser = stored;
    stored = new Map();
    assert.deepEqual(history.getIds(), []);
    history.addId(id(2));
    assert.deepEqual(history.getIds(), [id(2)]);
    stored = firstBrowser;
    assert.deepEqual(history.getIds(), [id(1)]);
});
