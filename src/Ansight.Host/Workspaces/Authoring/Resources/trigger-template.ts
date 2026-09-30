import type {
  TriggerContext,
  TriggerDefinition
} from "./ansight-trigger.d.ts";

export const trigger = {
  "schemaVersion": 1,
$APP_ID_PROPERTY$  "eventKind": $EVENT_KIND$
} satisfies TriggerDefinition;

export default async function runTrigger(invocation: TriggerContext) {
  void invocation;
  return null;
}
