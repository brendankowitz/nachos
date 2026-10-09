/*
EXECUTE on the number order-key functions for every database user. Filters call them (FilterWriter in
Nachos.DataLayer.SqlServer) on behalf of the application's identity, which in Azure has only db_datareader, db_datawriter
and db_ddladmin: none of those roles carries EXECUTE, so without this every numeric metadata filter fails with error 229.
Both functions are pure and deterministic (they read no table), so granting them to public discloses nothing.
*/
GRANT EXECUTE ON OBJECT::[dbo].[JsonNumberOrderKey] TO [public];
GO
GRANT EXECUTE ON OBJECT::[dbo].[JsonNumberOrderKeyLong] TO [public];
GO
