"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.canonicalExtractionEdges = canonicalExtractionEdges;
const compare = (left, right) => left < right ? -1 : left > right ? 1 : 0;
function canonical(value) {
    if (Array.isArray(value))
        return value.map(canonical);
    if (value !== null && typeof value === 'object')
        return Object.fromEntries(Object.entries(value)
            .filter(([, value]) => value !== undefined).sort(([left], [right]) => compare(left, right)).map(([key, value]) => [key, canonical(value)]));
    return value;
}
/** One raw extraction edge per identity supported by persistence. Framework passes must see
 * their original evidence first. Keep a deterministic existing property witness (prefer the
 * richest one), without rewriting its scalar hints, and preserve every differing semantic
 * property/provenance variant. Existing HTTP ambiguity and React render summaries pass intact.
 */
function canonicalExtractionEdges(edges) {
    const groups = new Map();
    for (const edge of edges) {
        const key = JSON.stringify([edge.sourceQN, edge.targetQN, edge.type]);
        if (!groups.has(key))
            groups.set(key, []);
        groups.get(key).push(edge);
    }
    return [...groups].sort(([left], [right]) => compare(left, right)).map(([, group]) => {
        if (group.length === 1)
            return group[0];
        const variants = new Map();
        for (const edge of group) {
            const properties = canonical(edge.properties ?? {});
            variants.set(JSON.stringify(properties), properties);
        }
        const sorted = [...variants].sort(([left], [right]) => compare(left, right));
        const primary = [...sorted].sort(([leftKey, left], [rightKey, right]) => Object.keys(right).length - Object.keys(left).length || compare(leftKey, rightKey))[0][1];
        return { ...group[0], properties: { ...primary,
                ...(sorted.length > 1 ? { extraction_evidence: sorted.map(([, properties]) => properties) } : {}) } };
    });
}
