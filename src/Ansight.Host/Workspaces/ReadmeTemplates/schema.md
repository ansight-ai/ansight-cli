# Schemas

These JSON Schema files are the machine-readable rules for Ansight workspace
definitions. Humans write files in `tests/` and `trends/`; Ansight uses the
schemas to catch unknown fields, missing values, invalid limits, and unsupported
contract versions.

Read the full [Ansight workspace schemas guide](https://www.ansight.ai/docs/workspace/schemas).

The folder includes:

- `task-definition.v1.schema.json`
- `test-definition.v1.schema.json`
- `trends-definition.v1.schema.json`
- `trigger-definition.v1.schema.json`

The `.v1.` part is the contract version, not your app version. Most teams should
not edit these files. Validate through Ansight so referenced Trends IDs are
checked as well as individual JSON shapes:

```sh
ansight test validate .
```

If you are developing Ansight itself and changing a contract, update the source
schema artifact and its parser and tests together.
