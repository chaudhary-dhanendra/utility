.Artifacts[]
| select(.TargetName | test("^(sk0[1-6]fn_(getworkalloteddays|workdaysallot)|fn_diagramobjects|ins_histry_work_unfreez_mat_expn|ins_visitcalender|sau_sp_ccfreezeentry|sp_verify_kml|sau_sp_npci_mapperupdate_new|sau_sp_ombudspersondetailsreport|spjsactr_data|updt_amritsarovar_work|wg_getdataset)"; "i"))
| [.TargetSchema, .TargetName, .TargetObjectType, .Classification, (.RequiresManualReview | tostring), .RelativePath]
| @tsv
