function Get-CiPythonLibraryDirectory {
    param([Parameter(Mandatory)] [string] $PythonExecutable)

    if (-not [IO.Path]::IsPathFullyQualified($PythonExecutable) -or
        -not [IO.File]::Exists($PythonExecutable)) {
        throw 'The selected setup-python output must be an existing absolute interpreter path.'
    }
    $bin = [IO.Path]::GetDirectoryName($PythonExecutable)
    if ([IO.Path]::GetFileName($bin) -ne 'bin' -or
        [IO.Path]::GetFileName($PythonExecutable) -notmatch '^python(?:3(?:\.\d+)?)?$') {
        throw 'Expected the selected Linux setup-python interpreter under its bin directory.'
    }
    $library = Join-Path ([IO.Path]::GetDirectoryName($bin)) 'lib'
    if (-not [IO.Directory]::Exists($library)) { throw 'The selected Python library directory is missing.' }
    return $library
}

function Test-CiPythonRuntime {
    param([Parameter(Mandatory)] [string] $PythonExecutable)

    if (-not $IsLinux) { throw 'The hosted Python runtime check must execute on Linux.' }
    $library = Get-CiPythonLibraryDirectory $PythonExecutable
    if ($env:LD_LIBRARY_PATH -ne $library) { throw 'The selected Python library path was not reconstructed after clearing.' }
    $python3 = (Get-Command python3 -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    & $PythonExecutable -I -c 'import os, sys; assert sys.version_info[:2] == (3, 13); assert os.path.realpath(sys.executable) == os.path.realpath(sys.argv[1]); assert os.path.realpath(sys.argv[2]) == os.path.realpath(sys.executable); print(sys.version); print(sys.executable)' $PythonExecutable $python3
    if ($LASTEXITCODE -ne 0) { throw "Selected Python interpreter check failed with exit $LASTEXITCODE." }
    $venv = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../artifacts/ci/python-runtime/$([guid]::NewGuid().ToString('N'))"))
    try {
        & $PythonExecutable -I -m venv $venv
        if ($LASTEXITCODE -ne 0) { throw "Selected Python venv creation failed with exit $LASTEXITCODE." }
        & (Join-Path $venv 'bin/python') -I -c 'import os, sys; assert sys.version_info[:2] == (3, 13); assert sys.prefix != sys.base_prefix; assert os.path.realpath(sys.prefix) == os.path.realpath(sys.argv[1]); print("Selected Python venv runtime: PASS")' $venv
        if ($LASTEXITCODE -ne 0) { throw "Selected Python venv interpreter failed with exit $LASTEXITCODE." }
    }
    finally {
        if (Test-Path -LiteralPath $venv) { Remove-Item -LiteralPath $venv -Recurse -Force }
    }
}
