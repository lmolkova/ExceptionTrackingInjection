using System;
using System.Collections.Generic;
using System.Web.Http;

namespace ErrorHandlers.Controllers
{
    public class WebApiController : ApiController
    {
        // GET: api/webapi
        public IEnumerable<string> Get()
        {
            throw new ArgumentException("webapi");
            return new string[] { "value1", "value2" };
        }

        // GET: api/Default1/5
        public string Get(int id)
        {
            return "value";
        }

        // POST: api/Default1
        public void Post([FromBody]string value)
        {
        }

        // PUT: api/Default1/5
        public void Put(int id, [FromBody]string value)
        {
        }

        // DELETE: api/Default1/5
        public void Delete(int id)
        {
        }

    }
}
