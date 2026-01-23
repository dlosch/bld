I would like to publish this tool on nuget. It should be very good quality. 

I want to include the beta commands, but they should behave properly, never overwrite or change files without explicit consent (for most commands, --apply). validate the code for each command and service.

also, both the log output as well as the spectre console based output should be validated. I like the spectre consle based results, but the logging behavior differs between commands and should be made more consistent (the nuget command doesnt show progress for example)

the tfm command should explicitly mark end of life target frameworks

if tests are missing, create missing tests

create a plan to validate and improve the code quality of the tool and execute the plan
