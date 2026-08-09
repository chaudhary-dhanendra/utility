{
  objects: [
    .Objects[]
    | select(.SourceName | test("^SK0[1-4]MustRoll1718$"; "i"))
    | { id: .Id.Value, schema: .SourceSchema, name: .SourceName, type: .ObjectType, included: .IsIncluded }
  ],
  dependencies: [
    .Dependencies[]
    | select(.SourceObjectId.Value == "463da3fc-0805-59eb-8cd2-3d7a8f2a87be")
    | { target: .TargetObjectId.Value, referenced: .ReferencedName, external: .IsExternal, ambiguous: .IsAmbiguous }
  ]
}
