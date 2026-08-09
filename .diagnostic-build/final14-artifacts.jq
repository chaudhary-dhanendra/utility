def root_ids:
  ["b556dbfd-1ea4-5afe-97e2-b510df3f8af4", "c5b3ebb7-3037-542a-be75-2352d4f1aa37",
   "c9c6949c-0436-56fe-9100-ec57eccec5b8", "2eaf8ded-0a4b-5bc7-9d68-88a6d1a74cba",
   "18d25216-c712-5e91-91a7-e353825f5e65", "12fb7e38-20f9-5ea1-924c-f8f06138e7fc",
   "e4ca77f9-aa9a-552e-af75-a2dab8c5ce1f", "2a09c738-970e-56d6-a490-bc33f2395e5e",
   "3c654e08-71ef-50ba-a59c-71ad0cb261a5", "604885a9-a753-5076-a122-ca494d1a5f67",
   "463a5761-49b6-56c2-aa58-ff915bc26fc0", "75306665-33bb-5296-b41b-8b35c6c021f4",
   "a3ef1c8b-bdf0-5e26-a105-ee1185e88112", "463da3fc-0805-59eb-8cd2-3d7a8f2a87be"];

.Artifacts[]
| . as $artifact
| select(root_ids | index($artifact.SourceObjectId.Value))
| "===== \(.SourceObjectId.Value) | \(.RuleId) | class=\(.Classification) =====\nSOURCE:\n\(.SourceDefinition)\nGENERATED:\n\(.PostgreSqlDefinition)\nVALIDATION:\n\(.Validation | @json)\n"
