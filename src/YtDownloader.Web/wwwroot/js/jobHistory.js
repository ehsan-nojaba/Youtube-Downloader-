const key = "YtDownloader.jobIds";
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function getIds() {
    let ids;
    try { ids = JSON.parse(localStorage.getItem(key) ?? "[]"); }
    catch { ids = []; }
    if (!Array.isArray(ids)) return [];
    return [...new Set(ids.filter(id => typeof id === "string" && guid.test(id) &&
        id !== "00000000-0000-0000-0000-000000000000").map(id => id.toLowerCase()))].slice(0, 20);
}

export function addId(id) {
    if (!guid.test(id)) return;
    localStorage.setItem(key, JSON.stringify([id.toLowerCase(), ...getIds().filter(existing =>
        existing !== id.toLowerCase())].slice(0, 20)));
}
