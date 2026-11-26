#!/usr/bin/env python3
"""Compare published inventory with one Git commit, without running the suite.

Recognizes this runner's straight-line registration convention, not general C#.
Re-review the named support fingerprints if the runner changes its scaffolding.
"""

import argparse
import hashlib
import json
import re
import subprocess
import sys


PREFIX = "samples/deep-validation/"
IDENT = r"[A-Za-z_][A-Za-z_0-9]*"
NUMBER = r"(?:0[xX][0-9A-Fa-f_]+|[0-9][0-9_]*(?:\.[0-9][0-9_]*)?(?:[eE][+-]?[0-9][0-9_]*)?)[lLuUmMfFdD]*"
STRING = r'"(?:\\[^\r\n]|[^"\\\r\n])*"'
CHAR = r"'(?:\\[^\r\n]|[^'\\\r\n])+'"
CHAIN = rf"{IDENT}(?:\((?:{IDENT}(?:\.{IDENT})*)?\))?(?:\.{IDENT}(?:\((?:{IDENT}(?:\.{IDENT})*)?\))?)*"
HOLE = rf"\s*({CHAIN})\s*(?:,\s*-?\d+\s*)?(?::[A-Za-z0-9]+)?"
INTERP = re.compile(r'\$"(?:\\[^\r\n]|\{\{|\}\}|\{' + HOLE + r'\}|[^"\\{}\r\n])*"')
CHUNK = re.compile(
    rf"\s+|//[^\r\n]*|/\*[\s\S]*?\*/|{STRING}|{CHAR}|{IDENT}|{NUMBER}|"
    r"=>|\+\+|--|::|\?\.|[(){}\[\];:.,=<>?+*/%&|!~^\-]"
)
WRAPPERS = {
    "RunDirectoryProbe": 1, "RunScriptProbe": 1, "Expect": 2, "ExpectFloat": 2,
    "ExpectCompileRejected": 0, "ExpectDirectoryCompileRejected": 0,
    "ExpectBytecodeRejected": 0,
}
CASE_NAME = {"Run": 1, **WRAPPERS}
SUPPORT = {
    'Main header/setup': '11c47ef1ec87db48a0616de32ba85e55157c21f78ed203106a1d233631b209b2',
    'Main summary/return': 'd119cc9bff9cd1fa581359da37efe49dbdd52c27806833f907d5c0063f786cfa',
    'Run result accounting': '69ea6db6fa1a0cc48fa7ece364f787ab904aab5b4c2a28353e2240efbde43965',
    'PrintSummary': 'c1aaa21b26a278ccd4dc6ecce277fa23a9ea1ee551291ccc4ae4d9b62b53fe17',
    'Results declaration': '59c7e488c6f9394535b8bb76c265bb3610edd751de6bf7d29f349b424c9345d8',
}


def require(ok, problem):
    if not ok:
        raise ValueError(problem)


def tokenize(source):
    # Do not let a non-ASCII line break hide code inside an apparent // comment.
    require(not re.search(r"[\u0085\u2028\u2029]", source), "unsupported C# line separator")
    tokens = []
    holes = {}
    pos = 0
    while pos < len(source):
        interpolation = source.startswith('$"', pos)
        require(not source.startswith(('"""', '$"""', '/*'), pos) or
                (source.startswith('/*', pos) and source.find('*/', pos + 2) != -1),
                f"unsupported raw string or unterminated comment at offset {pos}")
        match = (INTERP if interpolation else CHUNK).match(source, pos)
        require(match, f"unsupported C# token or interpolation at offset {pos}")
        token = match[0]
        pos = match.end()
        if token.isspace() or token.startswith(("//", "/*")):
            continue
        if interpolation:
            # Skip escaped characters/braces first; only executable holes contribute names.
            fields = re.finditer(r"\\.|\{\{|\}\}|\{" + HOLE + r"\}", token[2:-1])
            holes[len(tokens)] = {name for field in fields if field[1]
                                 for name in re.findall(IDENT, field[1])}
        tokens.append(token)
    return tokens, holes


class Inventory:
    def __init__(self, source):
        self.t, self.holes = tokenize(source)
        self.end = {}
        self.depth = []
        stack = []
        close = {"(": ")", "{": "}", "[": "]"}
        for i, token in enumerate(self.t):
            self.depth.append(len(stack))
            if token in close:
                stack.append(i)
            elif token in close.values():
                require(stack and close[self.t[stack[-1]]] == token, "unbalanced Program.cs")
                self.end[stack.pop()] = i
        require(not stack, "unbalanced Program.cs")
        self.allowed = set()
        header = ["internal", "static", "class", "Program", "{"]
        starts = [i for i in range(len(self.t)) if self.t[i:i+5] == header]
        require(len(starts) == 1 and self.depth[starts[0]] == 0, "expected one top-level Program class")
        self.start = starts[0] + 4
        self.stop = self.end[self.start]

    def split(self, lo, hi, separator):
        start = i = lo
        while i < hi:
            if self.t[i] == separator:
                yield start, i
                start = i + 1
            i = self.end.get(i, i) + 1
        if start < hi:
            require(separator != ";", "expected complete top-level statement")
            yield start, hi

    def method(self, name, prefix, generic=False, parameters=True):
        t = self.t
        matches = []
        for i in range(self.start + 1, self.stop):
            if self.depth[i] != 1 or t[i] != name or t[i-len(prefix):i] != prefix:
                continue
            j = i + 1
            if generic and t[j:j+3] == ["<", "T", ">"]:
                j += 3
            if t[j] == "(" and (parameters or self.end[j] == j + 1):
                k = self.end[j] + 1
                if t[k] == "{":
                    matches.append((i, k, self.end[k]))
        require(len(matches) == 1, f"expected unique Program.{name} declaration")
        self.allowed.add(matches[0][0])
        return matches[0]

    def verified(self, name, start, stop):
        blob = json.dumps(self.t[start:stop], ensure_ascii=True, separators=(",", ":")).encode()
        digest = hashlib.sha256(blob).hexdigest()
        require(digest == SUPPORT.get(name), f"review changed {name} scaffolding")
        self.allowed.update(range(start, stop))

    def call(self, start, stop, names):
        t = self.t
        if t[start] != "await" or start + 2 >= stop or t[start+1] not in names:
            return None
        j = start + 2
        if t[j] == "<":
            j += 1
            while j < stop and t[j] != ">":
                if not re.fullmatch(IDENT, t[j]):
                    return None
                j += 1
                if j < stop and t[j] == "?":
                    j += 1
                if j < stop and t[j] == ",":
                    j += 1
                    if j < stop and t[j] == ">":
                        return None
                else:
                    break
            if j >= stop or t[j] != ">":
                return None
            j += 1
        if j >= stop or t[j] != "(" or self.end[j] != stop - 1:
            return None
        return t[start+1], list(self.split(j+1, stop-1, ","))

    def count(self):
        t = self.t
        main, lo, hi = self.method("Main", "public static async Task < int >".split(), parameters=False)
        statements = list(self.split(lo+1, hi, ";"))
        require(len(statements) >= 9, "expected Main scaffolding and dispatch")
        self.verified("Main header/setup", main-7, statements[5][1]+1)
        self.verified("Main summary/return", statements[-2][0], hi+1)
        groups = []
        for a, b in statements[6:-2]:
            require(b-a == 4 and t[a] == "await" and t[a+2:b] == ["(", ")"]
                    and re.fullmatch(r"Run[A-Za-z0-9_]+", t[a+1])
                    and t[a+1] not in CASE_NAME, "unsupported Main dispatch")
            self.allowed.add(a+1)
            groups.append(t[a+1])
        require(len(set(groups)) == len(groups), "repeated Main dispatch")

        prefix = "private static async Task".split()
        run, _, run_end = self.method("Run", prefix)
        summary, _, summary_end = self.method("PrintSummary", "private static void".split(), parameters=False)
        self.verified("Run result accounting", run-len(prefix), run_end+1)
        self.verified("PrintSummary", summary-3, summary_end+1)
        # Results field is the fixed first class member, not any arbitrary declaration.
        first_end = self.start + 1
        while first_end < self.stop and t[first_end] != ";":
            first_end += 1
        self.verified("Results declaration", self.start+1, first_end+1)

        for name in WRAPPERS:
            _, left, right = self.method(name, prefix, generic=True)
            outer = list(self.split(left+1, right, ";"))
            require(len(outer) == 1, f"{name} must contain exactly one outer await Run")
            a, b = outer[0]
            invocation = self.call(a, b, {"Run"})
            require(invocation is not None and len(invocation[1]) == 3,
                    f"{name} must delegate to one Run(area, name, body)")
            n0, n1 = invocation[1][1]
            require(t[n0:n1] == ["name"], f"{name} must forward the case name")
            self.allowed.add(a+1)

        counts = {}
        for group in groups:
            _, left, right = self.method(group, prefix, parameters=False)
            count = 0
            for a, b in self.split(left+1, right, ";"):
                invocation = self.call(a, b, CASE_NAME)
                if invocation is None:
                    self.verified_setup(group, a, b)
                    continue
                name, args = invocation
                index = CASE_NAME[name]
                require(len(args) > index, f"{group}: missing case name for {name}")
                n0, n1 = args[index]
                require(n1 == n0+1 and re.fullmatch(STRING, t[n0]) and t[n0] != '""',
                        f"{group}: expected a nonempty regular string case name for {name}")
                self.allowed.add(a+1)
                count += 1
            require(count > 0, f"{group}: no recognized cases")
            counts[group] = count

        guarded = {*groups, *CASE_NAME, "Results", "Main", "PrintSummary"}
        for i, token in enumerate(t):
            refs = guarded.intersection((token, *self.holes.get(i, ())))
            require(not refs or i in self.allowed,
                    f"unexpected guarded reference {', '.join(sorted(refs))} at token {i}")
        return counts

    def verified_setup(self, group, a, b):
        # Review the finite existing local scalar/runtime initializers separately from probes.
        blob = json.dumps(self.t[a:b], ensure_ascii=True, separators=(",", ":")).encode()
        digest = hashlib.sha256(blob).hexdigest()
        require(digest in SETUP, f"{group}: unsupported top-level statement near {' '.join(self.t[a:min(a+5,b)])}")


SETUP = {
    '0833cc10e8b99b49a2049ccde1ef8a774d4770cbc1a0501c0ed91d5ceaa4925e',  # await using var runtime = await BuildSingleFileRuntime ( Script ( "stable" , "main.ts" ) )
    '17e140a474466949d212060f72a1890657ccc153db76b37dc6a51fe5612593c8',  # await using var runtime = await BuildSingleFileRuntime ( Script ( "numeric" , "main.ts" ) )
    'b624eafae0a75e3051fc4c2ed6fec550df0c100628476cf08a3690b16f69135d',  # const ulong low = 9_223_372_036_854_775_900UL
    '31c3a5468bc6fdeb990c6d0d401c07702461158df7e90dae09ac4c6c38d50776',  # const ulong high = 18_446_744_073_709_551_000UL
    '3241f48dbf3790485656a7e938b95bd66655c7b1265c7ff92f10368984ff1c24',  # const ulong a = 4_000_000_000_000_000_000UL
    'cd32271321ada91c55919793946619ce79e7eaf6ab5e3afd5ec435a7f96fb96f',  # const ulong b = 1_000_000_000_000_000_000UL
    'd42b3094920d90ba8d8d2f200473b4fb3d3d1bedf7198a5706b01ac97c38f9c4',  # ulong expectedPipeline = ( ( a + b ) * 3UL - b ) / 2UL
    '51f2c197473591b6e1f66980803d9f8aef3a62dfa5b2bf6962adc5db8f3b8541',  # long bitA = 0x1234_5678_1122_3344L
    'b2b3f3226c9cf24e1d063cd55a57d959dfe100e6a9af61c1c10e804715b04e04',  # long bitB = 0x00FF_00FF_00FF_00FFL
    '8bb5650e061cb9f05d27a8a81d04902ab72fb48354adfb33f452529ba023f938',  # long bitExpected = ( ( bitA ^ bitB ) | ( bitA < < 4 ) ) & long . MaxValue
    'ae8a3b72c550e1348ed5c102959cbc486e0f829962720b3a0c9a55c19f122c09',  # decimal decA = 12.50m
    'df23c70a1f133203081397822bfc16773dd62fcd2030730de101de71d594bdeb',  # decimal decB = 0.25m
    'aade9193d8c6e6646bfe267c2ccfb95fe714d6cd5546ad004ad43056fbbc2604',  # decimal decC = 3m
    'd267a008f6ed750990e69b64e3a510c0ccd3caf1fe8b8fd80b57e5220950ded4',  # decimal decExpected = ( decA + decB ) * decC - decA / decC
    '247e316471a8a7d0d5034cb36b4aa041634c3341e011670c3ad19ade32d08587',  # var service = new TypedHostService ( )
    '4a97d92531a0af5ed6f7a0f1d8d77e179565152e9036f93b1fc55eeb827e51bc',  # await using var runtime = await new TypeSharpRuntimeBuilder ( ) . AddHostService ( "typed" , service ) . AddSourceFile ( Script ( "host" , "main.ts" ) ) . BuildAsync ( )
}


def fixtures(tree):
    count = 0
    folder = (PREFIX + "scripts/").encode()
    require(not tree or tree.endswith(b"\0"), "incomplete Git tree listing")
    for record in tree.split(b"\0")[:-1]:
        metadata, path = record.split(b"\t", 1)
        mode, kind, _ = metadata.split()
        if path in (b"samples", PREFIX.rstrip("/").encode(), folder[:-1]):
            require(kind == b"tree" and mode == b"040000",
                    f"expected directory in scripts/ path: {path!r}")
            continue
        require(path.startswith(folder), "unexpected path outside scripts/")
        if path.endswith(b".ts"):
            require(kind == b"blob" and mode in (b"100644", b"100755"),
                    f"unsupported tracked .ts entry: {path!r} ({mode.decode()})")
            count += 1
    return count


def unique_fields(pairs):
    obj = {}
    for key, value in pairs:
        require(key not in obj, f"manifest repeats key {key}")
        obj[key] = value
    return obj


def bad_json_constant(value):
    raise ValueError(f"manifest: invalid JSON constant {value}")


def discrepancies(readme, manifest, cases, scripts):
    banners = [line for line in readme.splitlines()
               if "pruebas independientes" in line or "archivos TypeSharp" in line]
    banner = re.fullmatch(r"\*\*([0-9]+) pruebas independientes · ([0-9]+) archivos TypeSharp\*\*",
                          banners[0]) if len(banners) == 1 else None
    require(banner, "README: expected exactly one well-formed inventory banner")
    parsed = json.loads(manifest, object_pairs_hook=unique_fields, parse_constant=bad_json_constant)
    require(isinstance(parsed, dict), "manifest: expected an object")
    metrics = {"test_cases": cases, "script_files": scripts}
    for key in metrics:
        require(type(parsed.get(key)) is int and parsed[key] >= 0,
                f"manifest: {key} must be a nonnegative integer")
    return [f"{publication} {key}: published {published[key]}, Git inventory {actual}"
            for publication, published in (
                ("README", dict(zip(metrics, map(int, banner.groups())))),
                ("manifest", parsed))
            for key, actual in metrics.items() if published[key] != actual]


def git(*args):
    try:
        return subprocess.run(["git", "--no-replace-objects", *args], check=True, capture_output=True).stdout
    except subprocess.CalledProcessError as exc:
        raise ValueError(f"git {args[0]}: {exc.stderr.decode(errors='replace').strip()}") from exc


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("ref", nargs="?", default="HEAD", help="Git commit-ish (default HEAD)")
    args = parser.parse_args()
    try:
        commit = git("rev-parse", "--verify", "--end-of-options", args.ref + "^{commit}").decode().strip()
        require(re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", commit), "invalid resolved Git commit")

        def source(filename):
            return git("cat-file", "blob", f"{commit}:{PREFIX}{filename}").decode("utf-8")

        groups = Inventory(source("Program.cs")).count()
        cases = sum(groups.values())
        scripts = fixtures(git("ls-tree", "--full-tree", "-r", "-t", "-z", commit, "--",
                               PREFIX + "scripts/"))
        errors = discrepancies(source("README.md"), source("validation-manifest.json"), cases, scripts)
        print(f"{commit}: {cases} named cases, {scripts} tracked TypeScript fixtures", flush=True)
        for error in errors:
            print(error, file=sys.stderr)
        return int(bool(errors))
    except (ValueError, OSError) as exc:
        print(f"inventory check: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
