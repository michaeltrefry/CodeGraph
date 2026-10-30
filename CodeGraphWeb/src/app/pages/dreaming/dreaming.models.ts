import { WorkActor } from '../knowledge/knowledge.models';

export interface DreamSettings {
  enabled: boolean;
  default_provider: string;
  default_model: string;
  max_turns: number;
  max_tokens_per_run: number;
  minimum_interval_minutes: number;
  schedule: string;
  input_cost_per_million: number;
  output_cost_per_million: number;
}
export interface DreamInstructionRevision { revision: number; content: string; edited_by: string; created_at: string; }
export interface DreamInstructions { slug: string; content: string; revision: number; history: DreamInstructionRevision[]; }
export interface DreamInstructionSet { base: DreamInstructions; project: DreamInstructions; }
export interface DreamAction {
  tool: string;
  arguments: unknown;
  result: unknown;
  evidence: unknown;
  created_at: string;
}
export interface DreamRun {
  id: string;
  project_id: string;
  trigger: string;
  initiator: WorkActor;
  status: string;
  started_at: string;
  ended_at?: string;
  provider: string;
  model: string;
  input_tokens: number;
  output_tokens: number;
  estimated_cost: number;
  base_instruction_revision: number;
  project_instruction_revision: number;
  actions: DreamAction[];
  report?: string;
  reason?: string;
}
export interface DreamRevertEntry { journal_id: number; entry_id: string; action: string; status: 'reverted' | 'conflict' | 'already_reverted'; reason?: string; }
export interface DreamRevertResult { run_id: string; entries: DreamRevertEntry[]; }
export interface DreamRecurrence { events: number; while_served: number; while_not_served: number; }
export interface DreamRuleEffect { entry_id: string; summary: string; changed_at: string; before: DreamRecurrence; after: DreamRecurrence; }
export interface DreamEffects { rules: DreamRuleEffect[]; }
