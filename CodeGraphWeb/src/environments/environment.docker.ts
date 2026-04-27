const publicApiOrigin = 'https://codegraph-api.trefry.net';
const isPublicTrefryHost = globalThis.location?.hostname === 'codegraph.trefry.net';

export const environment = {
  apiUrl: isPublicTrefryHost ? `${publicApiOrigin}/api` : '/api',
  baseUrl: isPublicTrefryHost ? publicApiOrigin : '',
};
