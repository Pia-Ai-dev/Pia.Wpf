'use strict';
// An AppContainer denies lstat on every ancestor of a granted folder: answer those as folders, resolve realpath by hand.
const fs = require('fs');
const path = require('path');

// The container denies a folder under every spelling, so drop a \\?\ or \\.\ prefix before comparing.
function plain(p) {
  const s = path.resolve(p instanceof URL ? require('url').fileURLToPath(p) : String(p));
  if (/^\\\\[?.]\\unc\\/i.test(s)) return path.resolve('\\\\' + s.slice(8));
  return /^\\\\[?.]\\[a-z]:\\/i.test(s) ? s.slice(4) : s;
}
function key(p) {
  const s = plain(p).toLowerCase();
  return s.endsWith(path.sep) ? s : s + path.sep;
}

const roots = (process.env.PIA_SANDBOX_ROOTS || '').split('|').filter(Boolean).map(key);
const denied = (e) => e && (e.code === 'EPERM' || e.code === 'EACCES');

function isAncestor(p) {
  if (typeof p !== 'string' && !(p instanceof URL) && !Buffer.isBuffer(p)) return false;
  const s = key(p);
  return roots.some((r) => r.startsWith(s) && r !== s);
}

const orig = {
  lstatSync: fs.lstatSync, statSync: fs.statSync, lstat: fs.lstat, stat: fs.stat, readlinkSync: fs.readlinkSync,
  realpathSync: fs.realpathSync, realpathSyncNative: fs.realpathSync.native,
  realpath: fs.realpath, realpathNative: fs.realpath.native,
  pLstat: fs.promises.lstat, pStat: fs.promises.stat, pRealpath: fs.promises.realpath,
};

// __dirname is Pia's support folder, which every Node start grants.
const dirStats = [null, null];
function fakeDir(opts) {
  const k = opts && opts.bigint ? 1 : 0;
  if (!dirStats[k]) dirStats[k] = orig.statSync(__dirname, k ? { bigint: true } : undefined);
  return dirStats[k];
}

function wrapSync(fn) {
  return function (p, opts) {
    try { return fn.call(fs, p, opts); } catch (e) {
      if (!denied(e) || !isAncestor(p)) throw e;
      try { return fakeDir(opts); } catch { throw e; }
    }
  };
}
function wrapCb(fn) {
  return function (p, opts, cb) {
    if (typeof opts === 'function') { cb = opts; opts = undefined; }
    fn.call(fs, p, opts, (e, st) => {
      if (denied(e) && isAncestor(p)) { try { st = fakeDir(opts); } catch { return cb(e); } return cb(null, st); }
      cb(e, st);
    });
  };
}
function wrapP(fn) {
  return async function (p, opts) {
    try { return await fn.call(fs.promises, p, opts); } catch (e) {
      if (!denied(e) || !isAncestor(p)) throw e;
      try { return fakeDir(opts); } catch { throw e; }
    }
  };
}

// Ancestors of a root count as plain folders, even inside another root, so only links below them are followed.
function resolveLinks(start) {
  let s = start;
  for (let hops = 0; ; hops++) {
    const root = path.parse(s).root;
    const parts = s.slice(root.length).split(path.sep).filter(Boolean);
    let cur = root;
    let i = 0;
    while (i < parts.length && isAncestor(path.join(cur, parts[i]))) cur = path.join(cur, parts[i++]);
    let next = null;
    for (; i < parts.length; i++) {
      const c = path.join(cur, parts[i]);
      if (orig.lstatSync(c).isSymbolicLink()) { next = path.resolve(cur, orig.readlinkSync(c), ...parts.slice(i + 1)); break; }
      cur = c;
    }
    if (next === null) return cur;
    if (hops >= 32) {
      throw Object.assign(new Error(`ELOOP: too many symbolic links encountered, realpath '${start}'`),
        { code: 'ELOOP', syscall: 'realpath', path: start });
    }
    s = next;
  }
}
function realFallback(p, opts) {
  const s = resolveLinks(plain(p));
  orig.statSync(s);
  const enc = typeof opts === 'string' ? opts : opts && opts.encoding;
  if (enc === 'buffer') return Buffer.from(s);
  return enc && enc !== 'utf8' && enc !== 'utf-8' ? Buffer.from(s).toString(enc) : s;
}
function wrapRealSync(fn) {
  return function (p, opts) {
    try { return fn.call(fs, p, opts); } catch (e) { if (denied(e)) return realFallback(p, opts); throw e; }
  };
}
function wrapRealCb(fn) {
  return function (p, opts, cb) {
    if (typeof opts === 'function') { cb = opts; opts = undefined; }
    fn.call(fs, p, opts, (e, r) => {
      if (denied(e)) { try { r = realFallback(p, opts); } catch (e2) { return cb(e2); } return cb(null, r); }
      cb(e, r);
    });
  };
}

fs.lstatSync = wrapSync(orig.lstatSync);
fs.statSync = wrapSync(orig.statSync);
fs.lstat = wrapCb(orig.lstat);
fs.stat = wrapCb(orig.stat);
fs.realpathSync = wrapRealSync(orig.realpathSync);
fs.realpathSync.native = wrapRealSync(orig.realpathSyncNative);
fs.realpath = wrapRealCb(orig.realpath);
fs.realpath.native = wrapRealCb(orig.realpathNative);
fs.promises.lstat = wrapP(orig.pLstat);
fs.promises.stat = wrapP(orig.pStat);
fs.promises.realpath = async function (p, opts) {
  try { return await orig.pRealpath.call(fs.promises, p, opts); } catch (e) { if (denied(e)) return realFallback(p, opts); throw e; }
};
require('module').syncBuiltinESMExports();
