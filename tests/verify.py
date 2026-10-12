import hashlib, io, json, pathlib, struct, subprocess, time, zipfile, zlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
DATA = ROOT / 'work' / ('test-data-' + str(time.time_ns()))
DATA.mkdir(exist_ok=True, parents=True)
CLI = ROOT / 'bin' / 'EngineCli.exe'
RESULTS = []

def run(args, expected=0):
    p = subprocess.run([str(CLI), *map(str, args)], capture_output=True, timeout=40,
                       creationflags=subprocess.CREATE_NO_WINDOW)
    if p.returncode != expected:
        raise AssertionError(f'{args}: code={p.returncode}, {p.stdout!r}, {p.stderr!r}')
    return p

def test(name, fn):
    try:
        fn()
        RESULTS.append({'test': name, 'passed': True})
        print('PASS', name, flush=True)
    except Exception as e:
        RESULTS.append({'test': name, 'passed': False, 'error': str(e)})
        print('FAIL', name, str(e), flush=True)

def raw_zip(name, data=b'content', flags=0, attrs=0, method=0, zip64=False, descriptor=False, unicode_name=None, unicode_crc=None):
    packed = data if method == 0 else zlib.compress(data)[2:-4]
    crc = zlib.crc32(data)
    flags |= 8 if descriptor else 0
    local = struct.pack('<I5H3I2H', 0x04034b50, 20, flags, method, 0, 0x21,
                        0 if descriptor else crc, 0 if descriptor else len(packed),
                        0 if descriptor else len(data), len(name), 0) + name + packed
    if descriptor:
        local += struct.pack('<4I', 0x08074b50, crc, len(packed), len(data))
    extra = struct.pack('<HHQQQ', 1, 24, len(data), len(packed), 0) if zip64 else b''
    if unicode_name is not None:
        encoded=unicode_name.encode('utf-8')
        extra += struct.pack('<HHBI',0x7075,5+len(encoded),1,zlib.crc32(name) if unicode_crc is None else unicode_crc)+encoded
    central = struct.pack('<I6H3I5H2I', 0x02014b50, 0x314, 45 if zip64 else 20,
                          flags, method, 0, 0x21, crc,
                          0xffffffff if zip64 else len(packed),
                          0xffffffff if zip64 else len(data), len(name), len(extra), 0,
                          0, 0, attrs, 0xffffffff if zip64 else 0) + name + extra
    tail = b''
    if zip64:
        tail = struct.pack('<IQ2H2I4Q', 0x06064b50, 44, 45, 45, 0, 0, 1, 1, len(central), len(local))
        tail += struct.pack('<IIQI', 0x07064b50, 0, len(local)+len(central), 1)
    tail += struct.pack('<I4H2IH', 0x06054b50, 0, 0,
                        65535 if zip64 else 1, 65535 if zip64 else 1,
                        0xffffffff if zip64 else len(central),
                        0xffffffff if zip64 else len(local), 0)
    return local + central + tail

def fixture(filename, content):
    p = DATA / filename
    p.write_bytes(content)
    return p

def check_valid(path, target, expected):
    run(['inspect', path])
    out = DATA / target
    run(['extract', path, out])
    for name, value in expected.items():
        p = out / name
        if value is None:
            assert p.is_dir()
        else:
            assert p.read_bytes() == value

normal = DATA / '中文示例.zip'
contents = {'项目说明.txt': '这是一个可逆的压缩实验。\n'.encode()*1000,
            '资料/参数.csv': b'time,value\n0,1\n1,2\n', '资料/空目录/': None,
            'binary.dat': bytes(range(256))*256, 'empty.txt': b''}
with zipfile.ZipFile(normal, 'w', zipfile.ZIP_DEFLATED) as z:
    for name, value in contents.items(): z.writestr(name, b'' if value is None else value)
test('UTF-8, Deflate, nested directories, empty file', lambda: check_valid(normal, 'normal-out', contents))
test('Stored entry', lambda: check_valid(fixture('stored.zip', raw_zip(b'a.txt')), 'stored-out', {'a.txt': b'content'}))
test('Legacy GBK filename', lambda: check_valid(fixture('gbk.zip', raw_zip('中文.txt'.encode('gbk'))), 'gbk-out', {'中文.txt': b'content'}))
test('ZIP64 central directory and local offset', lambda: check_valid(fixture('zip64.zip', raw_zip(b'zip64.txt', zip64=True)), 'zip64-out', {'zip64.txt': b'content'}))
test('Streaming data descriptor', lambda: check_valid(fixture('descriptor.zip', raw_zip(b'stream.txt', method=8, descriptor=True)), 'descriptor-out', {'stream.txt': b'content'}))
empty = DATA / 'empty.zip'
with zipfile.ZipFile(empty, 'w'): pass
test('Empty ZIP', lambda: check_valid(empty, 'empty-out', {}))
test('Validated Unicode Path extra field', lambda: check_valid(fixture('unicode-extra.zip',raw_zip(b'legacy.txt',unicode_name='扩展中文.txt')),'unicode-extra-out',{'扩展中文.txt':b'content'}))
test('Invalid Unicode Path CRC falls back to original name', lambda: check_valid(fixture('unicode-invalid.zip',raw_zip(b'legacy.txt',unicode_name='错误.txt',unicode_crc=0)),'unicode-invalid-out',{'legacy.txt':b'content'}))

def reject(name, payload, inspect=True):
    p=fixture(name+'.zip', payload);dest=DATA/(name+'-out')
    run(['inspect',p] if inspect else ['extract',p,dest],2)
    assert not dest.exists()
    assert not list(DATA.glob(dest.name+'.partial-*'))

test('Reject path traversal', lambda: reject('traversal', raw_zip(b'../outside.txt')))
test('Reject absolute path', lambda: reject('absolute', raw_zip(b'/outside.txt')))
test('Reject drive and alternate stream path', lambda: reject('ads', raw_zip(b'file.txt:stream')))
test('Reject reserved device name', lambda: reject('device', raw_zip(b'CON.txt')))
test('Reject trailing space alias', lambda: reject('space', raw_zip(b'a.txt ')))
test('Reject symlink entry', lambda: reject('symlink', raw_zip(b'link', attrs=0o120777<<16)))
test('Reject encrypted flag', lambda: reject('encrypted', raw_zip(b'secret', flags=1)))
test('Reject unsupported compression', lambda: reject('method', raw_zip(b'unsupported', method=12)))
test('Reject truncated archive', lambda: reject('truncated', normal.read_bytes()[:-5]))
test('Reject traversal in Unicode Path extra field', lambda: reject('unicode-traversal',raw_zip(b'legacy.txt',unicode_name='../outside.txt')))

def conflicting(entries, label):
    p=DATA/(label+'.zip')
    with zipfile.ZipFile(p,'w') as z:
        for e in entries:z.writestr(e,b'v')
    reject(label,p.read_bytes())
test('Reject case-insensitive duplicates', lambda: conflicting(['A.txt','a.TXT'],'duplicates'))
test('Reject file-directory collision', lambda: conflicting(['a','a/b.txt'],'collision'))
damaged=bytearray(raw_zip(b'a.txt',b'correct'))
damaged[30+len(b'a.txt')]^=1
test('CRC failure discards all staged output', lambda: reject('bad-crc',damaged,False))

def cancel_extract():
    p=fixture('large.zip',raw_zip(b'large.bin',bytes(range(256))*2048));dest=DATA/'cancel-out'
    run(['cancel-extract',p,dest],3)
    assert not dest.exists() and not list(DATA.glob('cancel-out.partial-*'))
test('Cancellation removes uncommitted extraction',cancel_extract)

def no_overwrite():
    out=DATA/'protected';out.mkdir();(out/'keep.txt').write_text('keep')
    run(['extract',normal,out],2)
    assert (out/'keep.txt').read_text()=='keep'
test('Existing extraction folder is preserved',no_overwrite)

source=DATA/'source';source.mkdir();(source/'空目录').mkdir();(source/'说明.txt').write_text('中文内容\n'*2000,encoding='utf-8');(source/'binary.bin').write_bytes(bytes(range(256))*4096)
def create_roundtrip(store=False):
    dest=DATA/('created-store.zip' if store else 'created.zip')
    run(['store' if store else 'create',dest,source])
    with zipfile.ZipFile(dest) as z:
        assert z.testzip() is None
        assert z.read('source/说明.txt')==(source/'说明.txt').read_bytes()
        assert z.read('source/binary.bin')==(source/'binary.bin').read_bytes()
        assert 'source/空目录/' in z.namelist()
    run(['inspect',dest])
    run(['extract',dest,DATA/('created-store-out' if store else 'created-out')])
test('Created ZIP independently verified by Python',create_roundtrip)
test('Store mode independently verified by Python',lambda:create_roundtrip(True))

def cancel_create():
    dest=DATA/'cancel-created.zip';dest.write_bytes(normal.read_bytes());before=hashlib.sha256(dest.read_bytes()).hexdigest()
    run(['cancel-create',dest,source],3)
    assert hashlib.sha256(dest.read_bytes()).hexdigest()==before
    assert not list(DATA.glob('cancel-created.zip.partial-*'))
test('Cancelled creation preserves existing archive',cancel_create)

WINRAR=pathlib.Path(r'C:\Program Files\WinRAR\WinRAR.exe')
def winrar(args):
    p=subprocess.run([str(WINRAR),*map(str,args)],capture_output=True,timeout=60,creationflags=subprocess.CREATE_NO_WINDOW)
    assert p.returncode==0,(args,p.returncode,p.stderr)
def winrar_creates():
    dest=DATA/'winrar-created.zip'
    winrar(['a','-afzip','-ep1','-inul','-r',dest,str(source/'*')])
    with zipfile.ZipFile(dest) as z:
        files={i.filename:z.read(i) for i in z.infolist() if not i.is_dir()}
    run(['extract',dest,DATA/'winrar-created-out'])
    for name,value in files.items():assert (DATA/'winrar-created-out'/name).read_bytes()==value
if WINRAR.is_file():
    test('WinRAR creates ZIP, QingZip extracts it',winrar_creates)
else:
    print('SKIP WinRAR creates ZIP: WinRAR is not installed',flush=True)
def winrar_extracts():
    dest=DATA/'winrar-extract-out';dest.mkdir()
    winrar(['x','-inul','-o-',DATA/'created.zip',str(dest)+'\\'])
    assert (dest/'source'/'说明.txt').read_bytes()==(source/'说明.txt').read_bytes()
    assert (dest/'source'/'binary.bin').read_bytes()==(source/'binary.bin').read_bytes()
if WINRAR.is_file():
    test('QingZip creates ZIP, WinRAR extracts it',winrar_extracts)
else:
    print('SKIP WinRAR extracts ZIP: WinRAR is not installed',flush=True)

(ROOT/'work/test-results.json').write_text(json.dumps(RESULTS,ensure_ascii=False,indent=2),encoding='utf-8')
(ROOT/'work/test-fixtures.json').write_text(json.dumps({'data_root':str(DATA),'sample_zip':str(normal)},ensure_ascii=False),encoding='utf-8')
assert all(r['passed'] for r in RESULTS), 'Some tests failed; see test-results.json'
print(f'ALL {len(RESULTS)} TESTS PASSED',flush=True)
