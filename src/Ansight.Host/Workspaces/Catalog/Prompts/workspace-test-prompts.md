<!-- template:runner -->
Test scenario:
$SCENARIO$$VALIDATION_SECTION$$ASSERTIONS_SECTION$$SECRETS_SECTION$

Verify every check requested in the scenario, validation, or assertions at the point requested in the journey. Passed task assertions and relevant observations already returned by Ansight count as verification; successful actions alone do not prove their outcomes. Reuse that evidence instead of repeating a check or issuing a separate assertion call to restate it.

Check a toast, transient confirmation, page identity, or final state only when the test requests it. Capture a requested transient check when it occurs. Earlier evidence still proves an earlier checkpoint; recheck a final-state requirement only if later actions could have invalidated it. These runner instructions add no new product requirements.

Once all requested actions and checks are covered, call complete_instruction immediately. Continue only for a specific requested action or check that remains unproven. Fail if a requested check cannot be proven.
<!-- /template -->

<!-- template:validation -->
Validation:
$VALIDATION$
<!-- /template -->

<!-- template:assertions -->
Required assertions:
$ASSERTIONS$
<!-- /template -->

<!-- template:secrets -->
Host-managed secrets available to this test:
$SECRETS$
Use ansight_type_secret with one of these aliases. Never request or reveal its value.
<!-- /template -->
