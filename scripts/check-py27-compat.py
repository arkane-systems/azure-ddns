"""Static Python 2.7 compatibility check for the Unifi client (no Python 2 interpreter available locally).

Parses the script with Python 3's ast and rejects anything that is Python 3-only syntax, plus a handful of
Python 3-only library calls. Not a substitute for running under 2.7.18, but catches the usual mistakes.
"""
import ast
import sys

path = sys.argv[1]
tree = ast.parse(open(path, encoding='utf-8').read(), filename=path)

py3_only_nodes = tuple(getattr(ast, name) for name in (
    'JoinedStr', 'FormattedValue', 'AnnAssign', 'Nonlocal', 'YieldFrom', 'AsyncFunctionDef', 'AsyncFor',
    'AsyncWith', 'Await', 'MatMult', 'NamedExpr', 'Starred', 'TypeAlias', 'Match') if hasattr(ast, name))

problems = []
for node in ast.walk(tree):
    if isinstance(node, py3_only_nodes):
        problems.append((node.lineno, 'py3-only syntax: {}'.format(type(node).__name__)))
    if isinstance(node, ast.FunctionDef):
        a = node.args
        if a.kwonlyargs or a.posonlyargs:
            problems.append((node.lineno, 'keyword-only/positional-only parameters'))
        if node.returns is not None or any(x.annotation for x in a.args):
            problems.append((node.lineno, 'type annotations'))
    if isinstance(node, ast.Raise) and node.cause is not None:
        problems.append((node.lineno, 'raise ... from ...'))
    if isinstance(node, ast.Constant) and isinstance(node.value, bytes):
        pass  # b'' literals are fine in 2.7
    if isinstance(node, ast.Call):
        func = node.func
        name = ast.unparse(func)
        banned = ('subprocess.run', 'os.makedirs', 'shutil.which')
        if name == 'subprocess.run' or name == 'shutil.which':
            problems.append((node.lineno, 'py3-only call: ' + name))
        for kw in node.keywords:
            if kw.arg in ('text', 'encoding', 'errors', 'capture_output', 'exist_ok', 'universal_newlines') and kw.arg != 'universal_newlines':
                problems.append((node.lineno, 'py3-only keyword argument {} in {}'.format(kw.arg, name)))
    if isinstance(node, (ast.Import, ast.ImportFrom)):
        mods = [alias.name for alias in node.names] if isinstance(node, ast.Import) else [node.module]
        for m in mods:
            if m and (m.startswith('urllib.') or m in ('configparser', 'pathlib', 'typing', 'enum', 'ipaddress')):
                problems.append((node.lineno, 'py3-only module import: ' + m))

if problems:
    for line, msg in sorted(problems):
        print('line {}: {}'.format(line, msg))
    sys.exit(1)
print('no Python 3-only constructs found in', path)
