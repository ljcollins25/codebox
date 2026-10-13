function deadlineUtc(text) {
  if (typeof text !== 'string' || !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)?$/.test(text))
    throw new Error('invalid deadline');
  const zoned = /(?:Z|[+-]\d\d:\d\d)$/.test(text) ? text : text + 'Z';
  const ms = Date.parse(zoned);
  if (!Number.isFinite(ms)) throw new Error('invalid deadline');
  return ms;
}
module.exports = { deadlineUtc };
