/** The members of a work actor the label and tooltip read; the knowledge and work models both satisfy it. */
export interface ActorLabelSource {
  name: string;
  kind: string;
  session_id?: string;
  harness?: string;
  model?: string;
  host?: string;
}

/** `harness / model`, leaving out whichever is missing. */
function tool(actor: ActorLabelSource): string {
  return [actor.harness, actor.model].filter(Boolean).join(' / ');
}

/**
 * Who wrote a row. A person or the system is the name; an agent is `name (agent)` followed by
 * `harness / model @ host` when it wrote in a session; a legacy session is `harness / model @ host`.
 */
export function actorLabel(actor: ActorLabelSource | null | undefined): string {
  if (!actor) return 'unknown';
  if (actor.kind === 'agent') {
    const detail = tool(actor);
    const host = actor.host ? ` @ ${actor.host}` : '';
    return `${actor.name} (agent)${detail ? ' · ' : ''}${detail}${host}`;
  }
  if (actor.kind !== 'session') return actor.name;
  const detail = tool(actor) || actor.name;
  return actor.host ? `${detail} @ ${actor.host}` : detail;
}

/** The title (tooltip) of an actor: the identity and, for a session, its id. */
export function actorTitle(actor: ActorLabelSource | null | undefined): string {
  if (!actor) return '';
  return actor.session_id ? `${actor.name} (session ${actor.session_id})` : actor.name;
}
