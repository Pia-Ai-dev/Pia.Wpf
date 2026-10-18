'use strict';
// Inside an AppContainer every ancestor of a granted folder fails lstat with EPERM: answer those as folders and
// let realpath fall back to the resolved path, so Node's loader, npm and entry-point checks keep working.
const fs = require('fs');
const path = require('path');

const roots = (process.env.PIA_SANDBOX_ROOTS || '').split('|').filter(Boolean).map((r) => path.resolve(r).toLowerCase());
const denied = (e) => e && (e.code === 'EPERM' || e.code === 'EACCES');

function isAncestor(p) {
  if (typeof p !== 'string' && !(p instanceof URL) && !Buffer.isBuffer(p)) return false;
  let s = p instanceof URL ? require('url').fileURLToPath(p) : String(p);
  s = path.resolve(s).toLowerCase();
  if (!s.endsWith(path.sep)) s += path.sep;
  return roots.some((r) => (r + path.sep).startsWith(s) && (r + path.sep) !== s);
}

const orig = {
  lstatSync: fs.lstatSync, statSync: fs.statSync, lstat: fs.lstat, stat: fs.stat,
  realpathSync: fs.realpathSync, realpathSyncNative: fs.realpathSync.native,
  realpath: fs.realpath, realpathNative: fs.realpath.native,
  pLstat: fs.promises.lstat, pStat: fs.promises.stat, pRealpath: fs.promises.realpath,
};

let dirStats = null;
function fakeDir(opts) {
  if (!dirStats) dirStats = orig.statSync(roots[0] || process.execPath, opts);
  return dirStats;
}

function wrapSync(fn) {
  return function (p, opts) {
    try { return fn.call(fs, p, opts); } catch (e) { if (denied(e) && isAncestor(p)) return fakeDir(opts); throw e; }
  };
}
function wrapCb(fn) {
  return function (p, opts, cb) {
    if (typeof opts === 'function') { cb = opts; opts = undefined; }
    fn.call(fs, p, opts, (e, st) => { if (denied(e) && isAncestor(p)) return cb(null, fakeDir(opts)); cb(e, st); });
  };
}
function wrapP(fn) {
  return async function (p, opts) {
    try { return await fn.call(fs.promises, p, opts); } catch (e) { if (denied(e) && isAncestor(p)) return fakeDir(opts); throw e; }
  };
}

function realFallback(p) {
  const s = path.resolve(p instanceof URL ? require('url').fileURLToPath(p) : String(p));
  orig.statSync(s); // throws ENOENT for a missing leaf, as realpath would
  return s;
}
function wrapRealSync(fn) {
  return function (p, opts) {
    try { return fn.call(fs, p, opts); } catch (e) { if (denied(e)) return realFallback(p); throw e; }
  };
}
function wrapRealCb(fn) {
  return function (p, opts, cb) {
    if (typeof opts === 'function') { cb = opts; opts = undefined; }
    fn.call(fs, p, opts, (e, r) => {
      if (denied(e)) { try { return cb(null, realFallback(p)); } catch (e2) { return cb(e2); } }
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
  try { return await orig.pRealpath.call(fs.promises, p, opts); } catch (e) { if (denied(e)) return realFallback(p); throw e; }
};
require('module').syncBuiltinESMExports();
