# ctest helper: run x4mp-hostsim on a script that must fail; require exit code 1 and the "HOSTSIM FAIL line 3" line.
#   cmake -DHOSTSIM=<exe> -DDLL=<dll> -DSCRIPT=<file> -P expect_fail.cmake
execute_process(COMMAND "${HOSTSIM}" --dll "${DLL}" --ext-id x4mp_hostsim_stub --script "${SCRIPT}"
  RESULT_VARIABLE rc OUTPUT_VARIABLE out ERROR_VARIABLE err)
message("${out}${err}")
if(NOT rc EQUAL 1)
  message(FATAL_ERROR "expected exit code 1, got '${rc}'")
endif()
if(NOT out MATCHES "HOSTSIM FAIL line 3")
  message(FATAL_ERROR "expected a 'HOSTSIM FAIL line 3' line")
endif()
