--@name  Validate Object Names
--@id    validate-names
--@category Scripts
--@desc  A custom lint rule: flags level objects that have no script name.
--@api   1

-- Custom validator (2.5). lint.add contributes findings that merge with the built-in
-- linter; this is read-only, so it adds no undo step. Great as a project naming policy.

local flagged = 0
for _, o in ipairs(level.objects) do
  if o.name == "" then
    lint.add("warning", string.format("%s #%d has no script name", o.kind, o.uid), o.uid)
    flagged = flagged + 1
  end
end

local report = lint.run()
log.info(string.format("Contributed %d naming warning(s). Report total: %d error(s), %d warning(s).",
  flagged, report.error_count, report.warning_count))
return flagged
