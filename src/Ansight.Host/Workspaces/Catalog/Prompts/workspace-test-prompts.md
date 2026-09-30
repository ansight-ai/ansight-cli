<!-- template:runner -->
Test scenario:
$SCENARIO$$VALIDATION_SECTION$$ASSERTIONS_SECTION$$SECRETS_SECTION$

Treat every required assertion as mandatory. Verify the final state through Ansight tools before completing the test, and fail the test if any assertion cannot be proven.
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
